#load ".meta/globals.csx" // This is required for Intellisense in VS Code, etc. DO NOT TOUCH THIS LINE!
/*
Description: Useful skill for GitHub customers. Retrieve and assign issues. Get billing info.

Usage:
`@abbot github default {owner}/{repo}` - sets the default repository for this skill for the current room.
`@abbot github user {{mention}} is {{github-username}}` to map a chat user to their GitHub username.
`@abbot github issue triage [{owner}/{repo}]` - Retrieves unassigned issues. Limited to 20.
`@abbot github issue close #{number} [{owner}/{repo}]` - closes the specified issue.
`@abbot github issue #{number} [{owner}/{repo}]` - retrieves the issue by the issue number. `{owner}/{repo}` is not needed if the default repo is set.
`@abbot github issue assign #{number} to {assignee} [{owner}/{repo}]` - assigns {assignee} to the issue. {assignee} could be a chat user, a GitHub username without the `@` prefix, or "me".
`@abbot github issues assigned to {assignee}` - Retrieves open issues assigned to {assignee}. {assignee} could be a chat user, a GitHub username without the `@` prefix, or "me". 
`@abbot github billing {org-or-user}` - reports billing info for the specified organization or user. _Requires that a secret named `GitHubToken` be set with `admin:org` permission for orgs and `user` scope if reporting on a user._
*/
using Octokit;

// The key used to store and retrieve the default repository.
// Embedding the room name ensures the default repository is per-room so
// each room can have its own default.
readonly string DefaultRepositoryBrainKey = $"{Bot.Room}|DefaultRepository";

// GitHub Developer Token with access to the repository and org to query.
var githubToken = await Bot.Secrets.GetAsync("GitHubToken");
if (githubToken is not {Length: > 0}) {
    await Bot.ReplyAsync("This skill requires a GitHub Developer Token set up as a secret. "
         + "Visit https://github.com/settings/tokens to create a token. "
         + $"Then visit {Bot.SkillUrl} and click \"Manage skill secrets\" to add a secret named `GitHubToken` with the token you created at GitHub.com.");
    return;
}

// For GitHub Enterprise, pass in the Base URL after the ProductHeaderValue argument.
// TODO: Make the API url settable so GitHub Enterprise users don't have to edit the skill.
var github = new GitHubClient(new ProductHeaderValue("Abbot")) {
    Credentials = new Credentials(githubToken)
};

/*-----------------------------------
MAIN ENTRY POINT
*-----------------------------------*/
var (cmd, arg) = Bot.Arguments;

Task action = (cmd, arg) switch {
    ({Value: "default"}, _) => GetOrSetDefaultRepoAsync(Bot.Arguments.Skip(1)),
    ({Value: "billing"}, _) => ReplyWithBillingInfoAsync(Bot.Arguments.Skip(1)),
    ({Value: "issue"} or {Value: "issues"}, _) => HandleIssueSubCommandAsync(Bot.Arguments.Skip(1)),
    ({Value: "user"}, _) => MapGitHubUserToChatUser(Bot.Arguments.Skip(1)),
    _ => ReplyWithUsage()
};
await action;

if (!Bot.IsChat && !Bot.IsInteraction && !Bot.IsRequest) {
    // This was called by a schedule. Yes, we should have a property for this.
    await Bot.ReplyAsync("_Previous message brought to you by a scheduled `github` skill._");
}

async Task HandleIssueSubCommandAsync(IArguments arguments) {
    Task result = arguments switch {
        (IMissingArgument, _) => Bot.ReplyAsync("`@abbot help github` to learn how to use this skill."),
        ({Value: "triage"}, _) => ReplyWithIssueTriageAsync(arguments.Skip(1)),
        ({Value: "assign"}, _) => AssignIssueAndReplyAsync(arguments.Skip(1)),
        ({Value: "close"}, _) => CloseIssueAndReplyAsync(arguments.Skip(1)),
        ({Value: "assigned"}, _) => ReplyWithAssignedIssues(arguments.Skip(1)),
        ({Value: "user"}, _) => MapGitHubUserToChatUser(arguments.Skip(1)),
        var (issueNumber, repo) => ReplyWithIssueAsync(issueNumber, repo)
    };
    
    await result;
}

async Task MapGitHubUserToChatUser(IArguments args) {
    var (user, preposition, mapping) = args;
    if (mapping is IMissingArgument) { // Preposition is optional.
        mapping = preposition;
    }
    
    Task task = (user, mapping) switch {
        (IMissingArgument, _) or (_, IMissingArgument) => Bot.ReplyAsync("Please supply both a GitHub username and a chat user"),
        (IMentionArgument, IMentionArgument) => Bot.ReplyAsync("You specified two chat users. One should be a GitHub username without the `@` sign."),
        (IMentionArgument mention, var githubUser) => MapGitHubUserToChatUser(githubUser.Value, mention.Mentioned),
        (var githubUser, IMentionArgument mention) => MapGitHubUserToChatUser(githubUser.Value, mention.Mentioned),
        (_, _) => Bot.ReplyAsync("You specified two GitHub users. One should be a GitHub username without the `@` sign and the other should be a chat user mention."),
    };
    await task;
}

async Task MapGitHubUserToChatUser(string username, IChatUser chatUser) {
    var gitHubUsername = await EnsureGitHubUsername(username);
    if (gitHubUsername is null) {
        await Bot.ReplyAsync($"GitHub tells me {username} does not exist.");
    }
    await Bot.Brain.WriteAsync(GetUserMapKey(chatUser), gitHubUsername);
    await Bot.ReplyAsync($"Mapped {chatUser} to GitHub user {gitHubUsername}.");
}

async Task<string> GetGitHubUserNameForMention(IChatUser mentioned) {
    return await Bot.Brain.GetAsync(GetUserMapKey(mentioned));
}

string GetUserMapKey(IChatUser user) {
    return $"user:{user.Id}";
}

async Task CloseIssueAndReplyAsync(IArguments args) {
    var (numArg, prepositionArg, repositoryArg) = args;
    // We support @abbot github issue close #123 for aseriousbiz/blog or 
    // @abbot github issue close #123 aseriousbiz/blog (without the preposition).
    if (repositoryArg is IMissingArgument) {
        repositoryArg = prepositionArg;
    }
    
    var issueNumber = numArg.ToInt32();
    if (issueNumber is null) {
        await Bot.ReplyAsync("Please provide an issue number.");
        return;
    }
    
    var (owner, repo) = await GetRepoOrDefault(repositoryArg);

    if (owner is null || repo is null) {
        await Bot.ReplyAsync($"Please specify which repository this is for or set a default repository first. `@abbot help github` to learn more.");
        return;
    }
    
    var issue = await github.Issue.Get(owner, repo, issueNumber.Value);
    if (issue is null) {
        await Bot.ReplyAsync($"Could not find issue #{issueNumber} for {owner}/{repo} or I do not have access to it.");
        return;
    }
    var update = issue.ToUpdate();
    update.State = ItemState.Closed;
    await github.Issue.Update(owner, repo, issueNumber.Value, update);
    await Bot.ReplyAsync($"Closed issue #{issueNumber}.");
}

async Task AssignIssueAndReplyAsync(IArguments args) {
    var (numArg, prepositionArg, assigneeArg, repositoryArg) = args;
    // We support @abbot github issue assign #123 to haacked or 
    // @abbot github issue assign #123 haacked
    // (without the preposition).
    if (assigneeArg is IMissingArgument) {
        assigneeArg = prepositionArg;
    }
    
    var issueNumber = numArg.ToInt32();
    var (owner, repo) = await GetRepoOrDefault(repositoryArg);
    
    Task task = (issueNumber, assigneeArg, owner, repo) switch {
        (null, _, _, _) => Bot.ReplyAsync($"Please provide an issue number and an assignee. `-{numArg.Value}-` {assigneeArg}"),
        (_, IMissingArgument, _, _) => Bot.ReplyAsync("Please provide an assignee."),
        (_, _, _, _) => AssignIssue(issueNumber.Value, assigneeArg, owner, repo)
    };
    await task;
}

async Task AssignIssue(int issueNumber, IArgument assigneeArg, string owner, string repo) {
    if (owner is null || repo is null) {
        await Bot.ReplyAsync($"Please specify which repository this is for or set a default repository first. `@abbot help github` to learn more.");
        return;
    }
    
    var assignee = await GetAssigneeFromArgument(assigneeArg);
    if (assignee is null) {
        await Bot.ReplyAsync(GetUserNotFoundMessage(assigneeArg));
        return;
    }
    
    var issue = await github.Issue.Get(owner, repo, issueNumber);
    if (issue is null) {
        await Bot.ReplyAsync($"Could not find issue #{issueNumber} for {owner}/{repo} or I do not have access to it.");
        return;
    }
    
    var update = issue.ToUpdate();
    update.ClearAssignees();
    update.AddAssignee(assignee);
    await github.Issue.Update(owner, repo, issueNumber, update);
    await Bot.ReplyAsync($"Assigned {issueNumber} to {assignee}.");
}

static string GetUserNotFoundMessage(IArgument userArg) {
    return userArg is IMentionArgument mentioned
        ? $"I don't know the GitHub username for {userArg}. `@abbot github user {{mention}} is {{github-username}}` to tell me."
        : $"I could not find the GitHub user with the username `{userArg}. Either the user does not exist or the GitHub Token supplied to this skill does not have permissions for that user.";
}

async Task ReplyWithAssignedIssues(IArguments args) {
    var (prepositionArg, assigneeArg, forArg, repoArg) = args;
    // User can ask `@abbot github issues assigned to me` or `@abbot github issues assigned me` 
    if (prepositionArg is not {Value: "to"}) {
        // Move arguments up by one.
        repoArg = forArg;
        forArg = assigneeArg;
        assigneeArg = prepositionArg;
    }
    
    if (repoArg is IMissingArgument) {
        // User can ask `@abbot github issues assigned to me for aseriousbiz/abbot-skills` or `@abbot github issues assigned me aseriousbiz/abbot-skills` 
        repoArg = forArg;
    }
    
    var assignee = await GetAssigneeFromArgument(assigneeArg);
    if (assignee is null) {
        await Bot.ReplyAsync(GetUserNotFoundMessage(assigneeArg));
        return;
    }
    
    var (owner, repo) = await GetRepoOrDefault(repoArg);
    
    var request = new RepositoryIssueRequest {
        Assignee = assignee,
        State = ItemStateFilter.Open,
        Filter = IssueFilter.All
    };
    var apiOptions = new ApiOptions {
        PageSize = 20,
        PageCount = 1
    };
    var issues = await github.Issue.GetAllForRepository(owner, repo, request, apiOptions);
    if (issues is {Count: 0}) {
        var assigneeOut = assigneeArg is {Value: "me"}
            ? "you"
            : assignee;
        await Bot.ReplyAsync($"Good job! No open issues assigned to {assigneeOut}.");
        return;
    }
    var reply = issues.Select(FormatIssue).ToMarkdownList();
    await Bot.ReplyAsync(reply);
}

// Return all open and unassigend issues in the repository.
async Task ReplyWithIssueTriageAsync(IArguments args) {
    var (prepositionArg, repoArg) = args;
    if (repoArg is IMissingArgument) {
        repoArg = prepositionArg;
    }
    
    var (owner, repo) = await GetRepoOrDefault(repoArg);

    if (repo is null || owner is null) {
        await Bot.ReplyAsync("Repository must set as default or supplied in the form `owner/name`. `@abbot help github` for more information.");
        return;
    }
    var request = new RepositoryIssueRequest {
        Assignee = "none",
        State = ItemStateFilter.Open,
        Filter = IssueFilter.All
    };
    var apiOptions = new ApiOptions {
        PageSize = 20,
        PageCount = 1
    };
    var issues = await github.Issue.GetAllForRepository(owner, repo, request, apiOptions);
    if (issues is {Count: 0}) {
        await Bot.ReplyAsync("Good job! No issues to triage.");
        return;
    }
    var reply = issues.Select(FormatIssue).ToMarkdownList();
    await Bot.ReplyAsync(reply);
}

async Task ReplyWithIssueAsync(IArgument numArg, IArgument repository) {
    var (owner, repo) = await GetRepoOrDefault(repository);
    if (repo is null || owner is null) {
        await Bot.ReplyAsync("Repository must set as default or supplied in the form `owner/name`. `@abbot help github` for more information.");
        return;
    }
    
    var num = numArg.ToInt32();

    if (!num.HasValue) {
        await Bot.ReplyAsync("Issue number must be a number");
        return;
    }
    try {
        var issue = await github.Issue.Get(owner, repo, num.Value);
        await Bot.ReplyAsync($"{FormatIssue(issue)}\n{issue.Body}");
    }
    catch (NotFoundException) {
         await Bot.ReplyAsync($"Could not retrieve issue {num} in {owner}/{repo}.");   
    }
}

async Task ReplyWithBillingInfoAsync(IArguments args) {
    var (forArg, ownerArg) = args;
    if (ownerArg is IMissingArgument) {
        ownerArg = forArg;
    }
    
    if (ownerArg is IMissingArgument) {
        await Bot.ReplyAsync("Please specify an owner (user or org) to get billing info for. Ex: `@abbot github billing {owner}`.");
        return;
    }
    
    var isOrg = await IsOrgAsync(ownerArg.Value);
    
    var billingBaseApiUrl = github.BaseAddress
        + (isOrg ? "orgs" : "users")
        + $"/{ownerArg}/settings/billing/";
    
    var apiRequests = new Task<dynamic>[] {
        GetBillingInfoAsync(billingBaseApiUrl, "actions"),
        GetBillingInfoAsync(billingBaseApiUrl, "packages"),
        GetBillingInfoAsync(billingBaseApiUrl, "shared-storage")
    };
    
    var responses = await Task.WhenAll(apiRequests);
    var actions = responses[0];
    var packages = responses[1];
    var storage = responses[2];
    
    await Bot.ReplyAsync($@"Billing Info for `{ownerArg}`.
GitHub Actions:
```
Total Minutes Used: {actions.total_minutes_used}
Paid Minutes Used : {actions.total_paid_minutes_used}
Included Minutes  : {actions.included_minutes}
Minutes Used Breakdown: {actions.minutes_used_breakdown}
```

GitHub Packages:
```
Total Bandwidth Used (GB): {packages.total_gigabytes_bandwidth_used}
Paid Bandwidth Used (GB) : {packages.total_paid_gigabytes_bandwidth_used}
Included Bandwidth (GB)  : {packages.included_gigabytes_bandwidth}
```

GitHub Storage:
```
Days Left in Billing Cycle      : {storage.days_left_in_billing_cycle}
Estimated Paid Storage for Month: {storage.estimated_paid_storage_for_month}
Estimated Storage for Month     : {storage.estimated_storage_for_month}
```
");
}

bool isOrg = true; // Only applies to the Billing API call

async Task<dynamic> GetBillingInfoAsync(string baseApiUrl, string endpoint) {
    var headers = new Headers {
        {"Authorization", $"token {githubToken}"},
        {"Accept", "application/vnd.github.v3+json"}
    };
    var apiUrl = new Uri($"{baseApiUrl}{endpoint}");
    try {
        return await Bot.Http.GetJsonAsync(apiUrl, headers);
    }
    catch(HttpRequestException) {
        isOrg = false;
        
        return new {
            total_minutes_used = "unknown",
            total_paid_minutes_used = "unknown",
            included_minutes = "unknown",
            total_gigabytes_bandwidth_used = "unknown",
            total_paid_gigabytes_bandwidth_used = "unknown",
            included_gigabytes_bandwidth = "unknown",
            days_left_in_billing_cycle = "unknown",
            estimated_paid_storage_for_month = "unknown",
            estimated_storage_for_month = "unknown",
            minutes_used_breakdown = "unknown"
        };
    }
}

async Task<(string, string)> GetRepoOrDefault(IArgument arg) {
    var nameWithOwner = arg is IMissingArgument
        ? await GetDefaultRepoAsync()
        : arg.Value;
    return ParseNameWithOwner(nameWithOwner);
}

(string, string) ParseNameWithOwner(IArgument arg) {
    return ParseNameWithOwner(arg.Value);
}

(string, string) ParseNameWithOwner(string repository) {
    if (repository is null) {
        return (null, null);
    }
    var parts = repository.Split('/');
    if (parts is {Length: 2}) {
        return (parts[0], parts[1]);
    }
    return (null, null);
}

async Task ReplyWithUsage() {
    var usage = $@"`{Bot} help {Bot.SkillName}` to get help using this skill.";
    await Bot.ReplyAsync(usage);
}

string FormatIssue(Issue issue) {
    return $"[#{issue.Number} - {issue.Title} ({FormatAssignee(issue.Assignee)} - {issue.State})]({issue.HtmlUrl})";
}

string FormatAssignee(User user) {
    return user is null
        ? "unassigned"
        : "assigned to {user.Name}";
}

async Task<bool> IsOrgAsync(string org) {
    try {
        var organization = await github.Organization.Get(org);
        return organization is not null;
    }
    catch (NotFoundException) {
        return false;
    }
}

async Task GetOrSetDefaultRepoAsync(IArguments args) {
    var (isArg, repoArg) = args;
    
    if (repoArg is IMissingArgument) {
        // We support "@abbot default is aseriousbiz/abbot-skills" and "@abbot default aseriousbiz/abbot-skills"
        repoArg = isArg;
    }
    
    if (repoArg is IMissingArgument) {
        var currentDefault = await GetDefaultRepoAsync();
        if (currentDefault is null) {
            await Bot.ReplyAsync("There is no default repository set for this channel yet. `@abbot github default owner/repo` to set a default repository.");
        }
        else {
            await Bot.ReplyAsync($"The current default repository is `{currentDefault}`.");
        }
        return;
    }
    var (owner, repo) = ParseNameWithOwner(repoArg);
    if (owner is null || repo is null) {
        await Bot.ReplyAsync("To set a default repository, make sure the repository is provided in the `owner/repo` format.");
        return;
    }
    
    try {
        var repository = await github.Repository.Get(owner, repo);
    }
    catch (NotFoundException) {
        await Bot.ReplyAsync($"{repoArg} doesn't seem to be a repository or access is denied. Check your GitHub Token permissions.");
        return;
    }
    await WriteDefaultRepoAsync(repoArg.Value);
    await Bot.ReplyAsync($"`{repoArg}` is now the default repository.");
}

async Task<string> GetDefaultRepoAsync() {
    return await Bot.Brain.GetAsync(DefaultRepositoryBrainKey);
}

async Task WriteDefaultRepoAsync(string repo) {
    await Bot.Brain.WriteAsync(DefaultRepositoryBrainKey, repo);
}

async Task<string> GetAssigneeFromArgument(IArgument assigneeArg) {
    return assigneeArg switch {
        {Value: "me"} or {Value: "mi"} or {Value: "moi"} => await GetGitHubUserNameForMention(Bot.From),
        IMentionArgument mentioned => await GetGitHubUserNameForMention(mentioned.Mentioned),
        _ => await EnsureGitHubUsername(assigneeArg.Value)
    };
}

async Task<string> EnsureGitHubUsername(string username) {
    try {
        var user = await github.User.Get(username);
        return user.Name;
    }
    catch (NotFoundException) {
        return null;
    }
}