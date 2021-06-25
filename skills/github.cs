/*
Description: Useful skill for GitHub customers. Retrieve and assign issues. Get billing info.

Usage:
`@abbot github default {owner}/{repo}` - sets the default repository for this skill for the current room.
`@abbot github issue triage [{owner}/{repo}]` - Retrieves unassigned issues. Limited to 20.
`@abbot github issue #{number} [{owner}/{repo}]` - retrieves the issue by the issue number. `{owner}/{repo}` is not needed if the default repo is set.
`@abbot github issue assign #{number} to {assignee} [{owner}/{repo}]` - assigns {assignee} to the issue.
`@abbot github billing {org-or-user}` - reports billing info for the specified organization or user. _Requires that a secret named `GitHubToken` be set with `admin:org` permission for orgs and `user` scope if reporting on a user._

*/
using Octokit;

const string DefaultRepository = nameof(DefaultRepository);
bool isOrg = true;
var githubToken = await Bot.Secrets.GetAsync("GitHubToken");
var baseUrl = "https://api.github.com/";

if (githubToken is not {Length: > 0}) {
    await Bot.ReplyAsync("This skill requires a GitHub Developer Token set up as a secret. "
         + "Visit https://github.com/settings/tokens to create a token. "
         + $"Then visit {Bot.SkillUrl} and click \"Manage skill secrets\" to add a secret named `GitHubToken` with the token you created at GitHub.com.");
    return;
}

// For GitHub Enterprise, pass in the Base URL after the ProductHeaderValue argument.
var github = new GitHubClient(new ProductHeaderValue("Abbot")) {
    Credentials = new Credentials(githubToken)
};

var (cmd, arg) = Bot.Arguments;

Task action = (cmd, arg) switch {
        ({Value: "default"}, _) => GetOrSetDefaultRepoAsync(arg),
        ({Value: "billing"}, _) => ReplyWithBillingInfoAsync(arg),
        ({Value: "issue"}, _) => HandleIssueSubCommandAsync(Bot.Arguments.Skip(1)),
        ({Value: "user"}, _) => MapGitHubUserToChatUser(Bot.Arguments.Skip(1)),
        _ => ReplyWithUsage()
};
await action;

async Task HandleIssueSubCommandAsync(IArguments arguments) {
    Task result = arguments switch {
        (IMissingArgument, _) => Bot.ReplyAsync("`@abbot help github` to learn how to use this skill."),
        ({Value: "triage"}, _) => ReplyWithIssueTriageAsync(null),
        ({Value: "assign"}, _) => AssignIssueAndReplyAsync(arguments.Skip(1)),
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
        (IMissingArgument, _) => Bot.ReplyAsync("Please supply both a GitHub username and a chat user"),
        (_, IMissingArgument) => Bot.ReplyAsync("Please supply both a GitHub username and a chat user"),
        (IMentionArgument, IMentionArgument) => Bot.ReplyAsync("You specified two chat users. One should be a GitHub username without the `@` sign."),
        (IMentionArgument mention, var githubUser) => MapGitHubUserToChatUser(githubUser.Value, mention.Mentioned),
        (var githubUser, IMentionArgument mention) => MapGitHubUserToChatUser(githubUser.Value, mention.Mentioned),
        (_, _) => Bot.ReplyAsync("You specified two GitHub users. One should be a GitHub username without the `@` sign and the other should be a chat user mention."),
    };
    await task;
}

async Task MapGitHubUserToChatUser(string githubUsername, IChatUser chatUser) {
    try {
        var user = await github.User.Get(githubUsername);
        await Bot.Brain.WriteAsync(GetUserMapKey(chatUser), githubUsername);
        await Bot.ReplyAsync($"Mapped {chatUser} to GitHub user {githubUsername}.");
    }
    catch (NotFoundException) {
        await Bot.ReplyAsync($"GitHub tells me {githubUsername} does not exist.");
    }
}

async Task<string> GetGitHubUserNameForMention(IChatUser mentioned) {
    var username = await Bot.Brain.GetAsync(GetUserMapKey(mentioned));
    if (username is null) {
        await Bot.ReplyAsync($"I don't know the GitHub username for {mentioned}. `@abbot github user {{mention}} is {{github-username}}` to tell me.");
    }
    return username;
}

string GetUserMapKey(IChatUser user) {
    return $"user:{user.Id}";
}

async Task AssignIssueAndReplyAsync(IArguments args) {
    // Ignore the issue and assign arguments.
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
    
    var assignee = assigneeArg is IMentionArgument mentionArgument
        ? await GetGitHubUserNameForMention(mentionArgument.Mentioned)
        : assigneeArg.Value;
    
    if (assignee is null) {
        // GetGitHubUserNameForMention will have reported the problem.
        return;
    }
    
    var issue = await github.Issue.Get(owner, repo, issueNumber);
    if (issue is null) {
        await Bot.ReplyAsync($"Could not find issue #{issueNumber} for {owner}/{repo}.");
        return;
    }
    
    var update = issue.ToUpdate();
    update.ClearAssignees();
    update.AddAssignee(assignee);
    await github.Issue.Update(owner, repo, issueNumber, update);
    await Bot.ReplyAsync($"Assigned {issueNumber} to {assignee}.");
}

async Task ReplyWithIssueTriageAsync(string repository) {
    var (owner, repo) = ParseNameWithOwner(repository);

    if (repo is null || owner is null) {
        await Bot.ReplyAsync("Repository must set as default or supplied in the form `owner/name`. `@abbot help github` for more information.");
        return;
    }
    var request = new RepositoryIssueRequest {
         Assignee = "none",
        Milestone = "none",
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


async Task ReplyWithBillingInfoAsync(IArgument userOrOrgArg) {
    var userOrOrg = userOrOrgArg.Value;
    if (userOrOrgArg is IMissingArgument) {
        await Bot.ReplyAsync("Please specify an org or user to get billing info. `@abbot help github` for more information.");
        return;
    }
    
    var isOrg = await IsOrgAsync(userOrOrg);
    
    var baseApiUrl = baseUrl
        + (isOrg ? "orgs" : "users")
        + $"/{userOrOrg}/settings/billing/";
    
    var apiRequests = new Task<dynamic>[] {
        GetBillingInfoAsync(baseApiUrl, "actions"),
        GetBillingInfoAsync(baseApiUrl, "packages"),
        GetBillingInfoAsync(baseApiUrl, "shared-storage")
    };
    
    var responses = await Task.WhenAll(apiRequests);
    var actions = responses[0];
    var packages = responses[1];
    var storage = responses[2];
    
    await Bot.ReplyAsync($@"Billing Info for `{userOrOrg}`.
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
        var organization = await Bot.Http.GetJsonAsync(new Uri(baseUrl + $"orgs/{org}"));
        return organization is not null;
    }
    catch (HttpRequestException) {
        return false;
    }
}

async Task GetOrSetDefaultRepoAsync(IArgument nameWithOwner) {
    if (nameWithOwner is IMissingArgument) {
        var currentDefault = await GetDefaultRepoAsync();
        if (currentDefault is null) {
            await Bot.ReplyAsync("There is no default repository set for this channel yet. `@abbot github default owner/repo` to set a default repository.");
        }
        else {
            await Bot.ReplyAsync($"The current default repository is `{currentDefault}`.");
        }
        return;
    }
    var (owner, repo) = ParseNameWithOwner(nameWithOwner);
    if (owner is null || repo is null) {
        await Bot.ReplyAsync("To set a default repository, make sure the repository is provided in the `owner/repo` format.");
        return;
    }
    await WriteDefaultRepoAsync(nameWithOwner.Value);
    await Bot.ReplyAsync($"`{nameWithOwner}` is now the default repository.");
}

async Task<string> GetDefaultRepoAsync() {
    return await Bot.Brain.GetAsync(GetDefaultRepositoryStorageKey());
}

async Task WriteDefaultRepoAsync(string repo) {
    await Bot.Brain.WriteAsync(GetDefaultRepositoryStorageKey(), repo);
}

string GetDefaultRepositoryStorageKey() {
    return $"{Bot.Room}|{DefaultRepository}";
}
