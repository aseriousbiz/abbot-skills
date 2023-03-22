//
// Usage:
//   .yeet {payload} [to {environment}] [--force]
//
// A 'payload' can be:
//   * A PR URL
//   * A repo/branch specified: 'aseriousbiz/abbot/main'

using Newtonsoft.Json.Linq;
using System.Security.Cryptography;
using Octokit;

static List<string> YeetGifs = new List<string>() {
    "https://media.tenor.com/jSx1KiL3L2UAAAAC/yeet-lion-king.gif",
    "https://i.giphy.com/media/ycB36KeEsRj0kXjW4H/200w.gif",
    "https://i.imgflip.com/4ho2sb.gif",
    "https://i.giphy.com/media/5PhDdJQd2yG1MvHzJ6/giphy.webp",
};

var args = Bot.Arguments;
bool FindAndRemoveBoolean(string arg) {
    var (matchArg, newargs) = args.FindAndRemove(a => a.Value == arg);
    args = newargs;
    return matchArg.Value == arg;
}
var verbose = FindAndRemoveBoolean("--verbose");
var force = FindAndRemoveBoolean("--force");
var forget = FindAndRemoveBoolean("--forget");
var help = FindAndRemoveBoolean("--help");

if(help) {
    var message = 
        "Usage: `.yeet [--verbose] [--force] <payload> to <environment>`\n" +
        "  Deploys `<payload>` to `<environment>`\n" +
        "  A payload can be:\n" +
        "    * A repo and branch reference (e.g. `aseriousbiz/abbot/main`)\n" +
        "    * A Pull Request URL (eg. `https://github.com/aseriousbiz/abbot/pull/123`)\n" +
        "Usage: `.yeet --forget`\n" +
        "  Removes your GitHub token from my memory";
    await Bot.ReplyAsync(message, new MessageOptions() { To = Bot.Thread });
    return;
}

if(forget) {
    await Bot.Brain.DeleteAsync($"token:{Bot.From.Id}");
    await Bot.ReplyAsync("Ok! I forgot your token!");
    return;
}

var ephemeralTarget = new MessageTarget(new ChatAddress(ChatAddressType.Room, Bot.Room.Id, EphemeralUser: Bot.From.Id));
async Task SendIt(string message)
{
    await Bot.ReplyAsync(message, new MessageOptions() { To = Bot.Thread });
}

async Task SendEphemeral(string message)
{
    await Bot.ReplyAsync(message, new MessageOptions() { To = ephemeralTarget });
}

Task SendVerbose(string message) => verbose ? SendIt(message) : Task.CompletedTask;

(string Emoji, string Message) GetConclusion(CheckConclusion conclusion) {
    return conclusion switch {
        CheckConclusion.ActionRequired => (":black_circle:", "requires action"),
        CheckConclusion.Failure => (":red_circle:", "failed"),
        CheckConclusion.Success => (":large_green_circle:", "succeeded"),
        CheckConclusion.TimedOut => (":hourglass:", "timed out"),
        CheckConclusion.Cancelled => (":white_circle:", "cancelled"),
        CheckConclusion.Neutral => (":white_circle:", "was neutral"),
        CheckConclusion.Skipped => (":white_circle:", "was skipped"),
        CheckConclusion.Stale => (":white_circle:", "is stale"),
        _ => (":white_circle:", "had an unknown status"),
    };
}

var key = SHA256.HashData(Encoding.UTF8.GetBytes(await Bot.Secrets.GetAsync("master-key")));
var clientId = await Bot.Secrets.GetAsync("gh-client-id");

async Task<(string DeviceCode, int Interval)> PromptForLoginAsync() {
    var req = new HttpRequestMessage(HttpMethod.Post, new Uri("https://github.com/login/device/code"));
    req.Content = new FormUrlEncodedContent(new Dictionary<string, string>() {
        { "client_id", clientId },
        { "scope", "repo user" }
    });
    req.Headers.Add("Accept", "application/json");
    var resp = await client.SendAsync(req);
    if(!resp.IsSuccessStatusCode) {
        await SendEphemeral($"GitHub returned a {resp.StatusCode} error :(");
        var s = await resp.Content.ReadAsStringAsync();
        await SendEphemeral($"```\n{s}\n```");
        return (null, 0);
    }
    dynamic json = JObject.Parse(await resp.Content.ReadAsStringAsync());
    var userCode = (string)json.user_code;
    var deviceCode = (string)json.device_code;
    var verificationUrl = (string)json.verification_uri;
    await SendEphemeral($"Hey, I need a login token for you.\nGo to {verificationUrl} and enter the code `{userCode}`");
    return (deviceCode, (int)json.interval);
}

async Task<string> WaitForAuthorizationAsync(string deviceCode, int interval) {
    var started = DateTime.UtcNow;
    while((DateTime.UtcNow - started).TotalMinutes < 5) {
        await Task.Delay(interval * 1000);
        
        var codeReq = new HttpRequestMessage(HttpMethod.Post, new Uri("https://github.com/login/oauth/access_token"));
        codeReq.Content = new FormUrlEncodedContent(new Dictionary<string, string>() {
            { "client_id", clientId },
            { "device_code", deviceCode },
            { "grant_type", "urn:ietf:params:oauth:grant-type:device_code" }
        });
        codeReq.Headers.Add("Accept", "application/json");
        var codeResp = await client.SendAsync(codeReq);
        dynamic json = JObject.Parse(await codeResp.Content.ReadAsStringAsync());
        if(json.access_token != null) {
            return (string)json.access_token;
        } else if(json.error == "slow_down") {
            interval = (int)json.interval;
        } else if(json.error == "access_denied") {
            await SendEphemeral("You didn't authorize me, so I can't start your deployment :(");
            return null;
        } else if(json.error != "authorization_pending") {
            await SendEphemeral($"GitHub reported a `{json.error}` error");
            return null;
        }
    }
    await SendEphemeral("Sorry, you took too long, can you start again?");
    return null;
}

// Check if we have a token for this user
var encryptedToken = (string)await Bot.Brain.GetAsync($"token:{Bot.From.Id}");
var client = new HttpClient();
string token = null;
if(string.IsNullOrEmpty(encryptedToken)) {
    var (deviceCode, interval) = await PromptForLoginAsync();
    if(deviceCode == null) {
        return;
    }
    token = await WaitForAuthorizationAsync(deviceCode, interval);
    if(token == null) {
        return;
    }
    await SendEphemeral("Thanks! I got a token. I'll save it for next time.");
    await SendEphemeral("You can ask me to forget it by typing `.yeet --forget`");
    
    var iv = new byte[16];
    RandomNumberGenerator.Fill(iv);
    using var aes = Aes.Create();
    aes.Key = key;
    var encrypted = aes.EncryptCbc(Encoding.UTF8.GetBytes(token), iv, PaddingMode.PKCS7);
    var val = $"{Convert.ToBase64String(iv)}|{Convert.ToBase64String(encrypted)}";
    await Bot.Brain.WriteAsync($"token:{Bot.From.Id}", val);
} else {
    var splat = encryptedToken.Split("|");
    var iv = Convert.FromBase64String(splat[0]);
    var ciphertext = Convert.FromBase64String(splat[1]);
    using var aes = Aes.Create();
    aes.Key = key;
    token = Encoding.UTF8.GetString(aes.DecryptCbc(ciphertext, iv));
}

var github = new GitHubClient(new ProductHeaderValue("Abbot")) {
    Credentials = new Credentials(token)
};
var user = await github.User.Current();


var (payloadArg, toArg, envArg) = args;

var environment = "production";

if (payloadArg.ToString() is not { Length: >0 } payloadStr)
{
    await SendIt("You need to provide a payload to be deployed!");
    return;
}

if (toArg.ToString() is { Length: >0 } to)
{
    if (to != "to" || envArg.ToString() is not {Length: >0} env)
    {
        await SendIt("Usage: '.yeet {payload} [to {environment}]'");
        return;
    }
    environment = env;
}

if(!Payload.TryParse(payloadStr, out var payload)) {
    await Bot.ReplyAsync($"I don't understand the payload `{payloadStr}`");
}

var sha = await payload.ResolveCheckShaAsync(github);
var artifactLabel = await payload.GetArtifactLabel(github);
var deployRef = await payload.ResolveRefAsync(github);

await SendVerbose($"I'm going to deploy '{sha}' to '{environment}'");

// Get checks for the commit
var checkRuns = await github.Check.Run.GetAllForReference(payload.Owner, payload.Repository, sha);
if((checkRuns?.CheckRuns?.Count ?? 0) == 0) {
    await Bot.ReplyAsync($"I couldn't find any check runs for '{sha}' in '{payload.Owner}/{payload.Repository}'");
    return;
}

var success = true;
var messages = new List<string>();
foreach(var run in checkRuns.CheckRuns) {
    if(run.Status != CheckStatus.Completed) {
        success = false;
        messages.Add($":hourglass: Still waiting on {run.Name}");
    } else if(run.Conclusion != CheckConclusion.Skipped) {
        success &= run.Conclusion == CheckConclusion.Success;
        var (emoji, message) = GetConclusion(run.Conclusion.Value.Value);
        messages.Add($"{emoji} Check `{run.Name}` {message}");
    }
}

// Check review status if it's a PR
if(payload is BranchPayload { Branch: "main" }) {
    // It's always safe to deploy main
    messages.Add($":white_check_mark: Deploying `main` is auto-approved");
} else if(payload is BranchPayload { Branch: var b }) {
    success = false;
    messages.Add(":x: Can't deploy non-`main` branch: `{b}`. If it's a PR, give me the PR URL.");
} else if(payload is PullRequestPayload { PRNumber: var prNumber }) {
    var reviews = await github.PullRequest.Review.GetAll(payload.Owner, payload.Repository, prNumber);
    var approved = false;
    foreach(var review in reviews) {
        if(review.State == PullRequestReviewState.Approved) {
            approved = true;
            messages.Add($":approved-pr: Reviewer `@{review.User.Login}` approved!");
        } else if(review.State == PullRequestReviewState.ChangesRequested) {
            success = false;
            messages.Add($":request_change: Reviewer `@{review.User.Login}` requested changes");
        }
    }
    if(!approved) {
        success = false;
        messages.Add($":sob: Nobody has approved your change!");
    }
} else {
    success = false;
    messages.Add(":x: Unknown payload type");
}

await SendVerbose(string.Join("\n", messages));

if(!success) {
    await Bot.ReplyAsync("Cancelling deployment. Check runs have not succeeded.");
    return;
}

await SendVerbose($"Starting deployment of <https://github.com/aseriousbiz/abbot/commit/{sha}|{sha}> ...");

if(environment == "production") {
    await Bot.ReplyAsync("Cowardly refusing to deploy to production for now...");
    return;
}

var deployRoom = Bot.Rooms.GetTarget("C04DAH5KCS0");
var yeetImage = Bot.Utilities.CreateRandom().Next(YeetGifs.Count);
await Bot.ReplyWithImageAsync(
    YeetGifs[yeetImage],
    $"{Bot.From} is deploying {payload.HtmlUrl} to {environment} ...",
    "Yeet",
    options: new MessageOptions() { To = deployRoom });

// Grab the docker checks and convert them to deploy workflows
var deployWorkflows = checkRuns.CheckRuns
    .Where(r => r.Name.StartsWith("docker_"))
    .ToList();

// Kick off the deployments
foreach(var workflow in deployWorkflows) {
    var deployWorkflow = workflow.Name.Replace("docker_", "deploy_") + ".yaml";
    var headSha = workflow.HeadSha;
    await SendVerbose($"Starting workflow `{deployWorkflow}` with `ref={deployRef}` `environment={environment}` and `label={artifactLabel}`");
    await github.Connection.Post(
        new Uri($"https://api.github.com/repos/{payload.Owner}/{payload.Repository}/actions/workflows/{deployWorkflow}/dispatches"),
        new {
            @ref = deployRef,
            inputs = new {
                environment = environment,
                label = artifactLabel,
            },
        },
        "application/vnd.github+json");
}

abstract record Payload(string Owner, string Repository)
{
    static readonly Regex GitHubRecognizer = new Regex(@"^https://github.com/(?<owner>[^/]+)/(?<name>[^/]+)/pull/(?<pr>\d+)/?.*$");
    static readonly Regex BranchRecognizer = new Regex(@"^(?<owner>[^/]+)/(?<name>[^/]+)/(?<branch>[A-Za-z0-9-]+)$");
    public abstract string HtmlUrl { get; }
    public static bool TryParse(string value, out Payload payload)
    {
        if (GitHubRecognizer.Match(value) is { Success: true } m1 && int.TryParse(m1.Groups["pr"].Value, out var prNum))
        {
            payload = new PullRequestPayload(
                m1.Groups["owner"].Value,
                m1.Groups["name"].Value,
                prNum);
            return true;
        }
        else if(BranchRecognizer.Match(value) is {Success: true} m2)
        {
            payload = new BranchPayload(
                m2.Groups["owner"].Value,
                m2.Groups["name"].Value,
                m2.Groups["branch"].Value);
            return true;
        }
        payload = null;
        return false;
    }
    
    public abstract Task<string> GetArtifactLabel(IGitHubClient github);
    public abstract Task<string> ResolveCheckShaAsync(IGitHubClient github);
    public abstract Task<string> ResolveDeployShaAsync(IGitHubClient github);
    public abstract Task<string> ResolveRefAsync(IGitHubClient github);
}

record BranchPayload(string Owner, string Repository, string Branch): Payload(Owner, Repository)
{
    public override string HtmlUrl => $"https://github.com/{Owner}/{Repository}/commits/{Branch}";
    public override async Task<string> ResolveCheckShaAsync(IGitHubClient github)
    {
        var branch = await github.Repository.Branch.Get(Owner, Repository, Branch);
        return branch.Commit.Sha;
    }
    public override async Task<string> ResolveDeployShaAsync(IGitHubClient github)
    {
        var branch = await github.Repository.Branch.Get(Owner, Repository, Branch);
        return branch.Commit.Sha;
    }
    public override Task<string> GetArtifactLabel(IGitHubClient github) {
        return Task.FromResult($"branch-{Branch.Replace('/', '.')}");
    }
    public override Task<string> ResolveRefAsync(IGitHubClient client) {
        return Task.FromResult(Branch);
    }
}

record PullRequestPayload(string Owner, string Repository, int PRNumber): Payload(Owner, Repository)
{
    public override string HtmlUrl => $"https://github.com/{Owner}/{Repository}/pull/{PRNumber}";
    public override async Task<string> ResolveDeployShaAsync(IGitHubClient github)
    {
        var pr = await github.PullRequest.Get(Owner, Repository, PRNumber);
        return pr.MergeCommitSha;
    }
    public override async Task<string> ResolveCheckShaAsync(IGitHubClient github)
    {
        var pr = await github.PullRequest.Get(Owner, Repository, PRNumber);
        return pr.Head.Sha;
    }
    public override async Task<string> GetArtifactLabel(IGitHubClient github) {
        var pr = await github.PullRequest.Get(Owner, Repository, PRNumber);
        return $"branch-{pr.Head.Ref.Replace('/', '.')}";
    }
    public override async Task<string> ResolveRefAsync(IGitHubClient github) {
        var pr = await github.PullRequest.Get(Owner, Repository, PRNumber);
        return pr.Head.Ref;
    }
}
