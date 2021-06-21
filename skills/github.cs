/*
Description: A set of utilities for GitHub users and organizations.

Usage:
`@abbot github billing {org-or-user}` - reports billing info for the specified organization or user. _Requires that a secret named `GitHubToken` be set with `admin:org` permission for orgs and `user` scope if reporting on a user._
`@abbot github default {org-or-user}` - sets the user or org as the default for this skill.
*/

const string DefaultOrgOrUser = nameof(DefaultOrgOrUser);
bool isOrg = true;
var githubToken = await Bot.Secrets.GetAsync("GitHubToken");
var baseUrl = "https://api.github.com/";

if (githubToken is not {Length: > 0}) {
    await Bot.ReplyAsync("This skill requires a GitHub Developer Token set up as a secret. "
         + "Visit https://github.com/settings/tokens to create a token. "
         + $"Then visit {Bot.SkillUrl} and click \"Manage skill secrets\" to add a secret named `GitHubToken` with the token you created at GitHub.com.");
    return;
}

var (cmd, userOrOrg) = Bot.Arguments;

Task action = cmd switch {
        {Value: "default"} => SetDefaultOrgOrUserAsync(userOrOrg),
        {Value: "billing"} => ReplyWithBillingInfoAsync(userOrOrg),
        _ => ReplyWithUsage()
};
await action;

async Task<bool> IsOrgAsync(string org) {
    try {
        var organization = await Bot.Http.GetJsonAsync(new Uri(baseUrl + $"orgs/{org}"));
        return organization is not null;
    }
    catch (HttpRequestException) {
        return false;
    }
}

async Task SetDefaultOrgOrUserAsync(IArgument userOrOrg) {
    if (userOrOrg is IMissingArgument) {
        var currentDefault = await Bot.Brain.GetAsync(DefaultOrgOrUser);
        if (currentDefault is null) {
            await Bot.ReplyAsync("Please specify an org or user as the default. `@abbot help github` for more information.");
        }
        else {
            await Bot.ReplyAsync($"The current default org or user is `{currentDefault}`.");
        }
            return;
    }
    await Bot.Brain.WriteAsync(DefaultOrgOrUser, userOrOrg.Value);
    await Bot.ReplyAsync($"`{userOrOrg}` is now the default org or user.");
}

async Task ReplyWithBillingInfoAsync(IArgument userOrOrgArg) {
    var userOrOrg = userOrOrgArg.Value;
    if (userOrOrgArg is IMissingArgument) {
        userOrOrg = await Bot.Brain.GetAsync(DefaultOrgOrUser);
        if (userOrOrg is null) {
            await Bot.ReplyAsync("Please specify an org or user. Or set one as a default. `@abbot help github` for more information.");
            return;
        }
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

async Task ReplyWithUsage() {
    var usage = $@"`{Bot} help {Bot.SkillName}` to get help using this skill.";
    await Bot.ReplyAsync(usage);
}
