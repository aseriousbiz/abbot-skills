#load ".meta/globals.csx" // This is required for Intellisense in VS Code, etc. DO NOT TOUCH THIS LINE!
/*
Description: Manage deployments with the GitHub Deployment API

Package URL: https://ab.bot/packages/aseriousbiz/deploy

USAGE:

Manage GitHub deployments to deployment targets. A deployment target is a named environment such as 'stage', 'prod', etc.
* `@abbot deploy status [for {owner}/{repo}]` - List the status of most recent deployments.
* `@abbot deploy repo` - Displays the default repository for this room.
* `@abbot deploy repo {owner}/{repo}` - Sets the default repo for this room.
* `@abbot deploy list targets` - Lists available deployment targets.
* `@abbot deploy add target {target}` - Adds a deployment target.
* `@abbot deploy remove target {target}` - Removes a deployment target.
* `@abbot deploy {branch or SHA} to {target} [for {owner}/{repo}]` - Creates a GitHub deployment of branch/SHA to a deployment target. To force a deploy add `--forced` at the very end.
Note: You can specify a default repository for a room using `@abbot repo {owner}/{repo}`. that way you don't have to append each status command with `for {owner}/{repo}`.

GitHub Deploy skill was inspired by https://github.com/stephenyeargin/hubot-github-deployments
*/
using Octokit;

var githubToken = await Bot.Secrets.GetAsync("GitHubToken");

if (githubToken is not {Length: > 0}) {
    await Bot.ReplyAsync("This skill requires a GitHub Developer Token set up as a secret. "
         + "Visit https://github.com/settings/tokens to create a token. "
         + $"Then visit {Bot.SkillUrl} and click \"Manage skill secrets\" to add a secret named `GitHubToken` with the token you created at GitHub.com.");
    return;
}

if (Bot.Arguments is { Count: 0 }) {
    await ReplyWithUsage();
    return;
}

// For GitHub Enterprise, pass in the Base URL after the ProductHeaderValue argument.
var github = new GitHubClient(new ProductHeaderValue("Abbot")) {
    Credentials = new Credentials(githubToken)
};

var (cmd, subject, preposition, ownerAndRepo, forcedArg) = Bot.Arguments;

if (cmd.Value is "repo") {
    if (subject is IMissingArgument) {
        var repo = await GetDefaultRepository();
        if (repo is not null) {
            await Bot.ReplyAsync($"The default repository for this room is `{repo}`.");
        }
        else {
            await Bot.ReplyAsync($"No default repository is set for this room. Use `{Bot} {Bot.SkillName} repo {{owner}}/{{name}}` to set the repository.");
        }
    }
    else {
        var repo = subject.Value;
        await SetDefaultRepository(repo);
        await Bot.ReplyAsync($"I set the default repo for this room to `{repo}`.");
    }
    return;
}

if (cmd.Value is "status") {
    var deploymentId = subject.ToInt32();
    var repoArg = deploymentId is null
        ? ownerAndRepo
        : preposition;
    
    var (owner, repo) = await GetOwnerAndRepo(repoArg);
    if (repo is null) {
        return;
    }
    
    if (deploymentId is not null) {
        var statuses = await github.Repository.Deployment.Status.GetAll(owner, repo, deploymentId.Value);
        if (statuses is { Count: 0 }) {
            await Bot.ReplyAsync("No statuses available");
            return;
        }
        await Bot.ReplyAsync(statuses.Select(status => $"Status: {status.Description} ({status.CreatedAt}) / State: #{status.State}")
            .ToMarkdownList());
    }
    else {
        var deployments = await github.Repository.Deployment.GetAll(owner, repo);
        if (deployments is { Count: 0 }) {
            await Bot.ReplyAsync("No recent deploymments.");
            return;
        }
        await Bot.ReplyAsync(
            deployments.Select(deployment => $"Deployment {deployment.Id} ({deployment.CreatedAt}): User: {deployment.Creator.Login} / Action: Deploy / Ref: {deployment.Sha} / Description: (#{deployment.Description})")
            .ToMarkdownList());
        }
    return;
}

if (cmd.Value is "list") {
    if (subject.Value is "targets") {
        var targets = await GetDeploymentTargets();
        if (targets.Count is 0) {
            await Bot.ReplyAsync($"No deployment targets. `{Bot} {Bot.SkillName} add target {{target}}` to add a deployment target.");
            return;
        }
        await Bot.ReplyAsync(targets.ToMarkdownList());
    }
    else if (subject.Value is "branches") {
        var (owner, repo) = await GetOwnerAndRepo(ownerAndRepo);
        if (repo is null) {
            return;
        }
        var branches = await github.Repository.Branch.GetAll(owner, repo);
        await Bot.ReplyAsync(branches.Select(branch => $"{branch.Name}: {branch.Commit.Sha}").ToMarkdownList());
    }
    else {
        await Bot.ReplyAsync($"I can list `targets` and `branches`. Use `{Bot} help {Bot.SkillName}` for more details.");
    }
    return;
}

if (cmd.Value is "add" && subject.Value is "target") {
    if (preposition is IMissingArgument) {
        await Bot.ReplyAsync("Please specify a deployment target to add.");
        return;
    }
    var target = preposition.Value;
    await AddDeploymentTarget(target);
    await Bot.ReplyAsync($"Added {target} to the set of deployment targets.");
    return;
}

if (subject.Value is "to") {
    // Handle deployment
    var reference = cmd.Value;
    var target = preposition.Value;
    var targets = await GetDeploymentTargets();
    if (!targets.Contains(target)) {
        await Bot.ReplyAsync($"{target} is not in the available deployment targets. Use `{Bot} {Bot.SkillName} list targets`.");
        return;
    }
    var (owner, repo) = await GetOwnerAndRepo(ownerAndRepo);
    
    var deployment = new NewDeployment(reference) {
        Environment = target,
        Task = DeployTask.Deploy,
        Payload = new() {
            {"user", Bot.From.Name },
            {"room", Bot.Room.Name },
            {"skill", Bot.SkillName },
        },
        Description = $"{Bot.From.Name} deployed `{reference}` to `{target}`"
    };

    var forced = Bot.Arguments.Any(a => a.Value == "--force");
    if (forced) {
        await Bot.ReplyAsync("Forced? I hope you know what you're doing...");
        deployment.RequiredContexts = new System.Collections.ObjectModel.Collection<string>();
    }

    await Bot.ReplyAsync($"Creating deployment for ref {reference} to env: {target} for {owner}/{repo}.");
    var result = await github.Repository.Deployment.Create(owner, repo, deployment);
    await Bot.ReplyAsync(result.Description);
    return;
}

string roomKey = Bot.Room.Id ?? Bot.Room.Name;

async Task<string> GetDefaultRepository() {
    return await Bot.Brain.GetAsync($"{roomKey}_Repo");
}

Task SetDefaultRepository(string repo) {
    return Bot.Brain.WriteAsync($"{roomKey}_Repo", repo);
}

async Task<HashSet<string>> GetDeploymentTargets() {
    return await Bot.Brain.GetAsync($"{roomKey}_Targets") ?? new HashSet<string>();
}

async Task AddDeploymentTarget(string target) {
    var targets = await GetDeploymentTargets();
    targets.Add(target);
    await SaveDeploymentTargets(targets);
}

async Task RemoveDeploymentTarget(string target) {
    var targets = await GetDeploymentTargets();
    targets.Remove(target);
    await SaveDeploymentTargets(targets);
}

Task SaveDeploymentTargets(HashSet<string> targets) {
    return Bot.Brain.WriteAsync($"{roomKey}_Targets", targets);
}

async Task<(string, string)> GetOwnerAndRepo(IArgument argument) {
    var (owner, repo) = ((string)null, (string)null);
    if (argument is IMissingArgument) {
        var ownerAndRepo = await GetDefaultRepository();
        if (ownerAndRepo is null) {
            await Bot.ReplyAsync($"No default repository set. Use `{Bot} {Bot.SkillName} set default repo {{owner/repo}}` to set a default repo.");
            return (owner, repo);
        }
        (owner, repo) = SplitOwnerAndRepo(ownerAndRepo);
    }
    else if (argument is IArguments arguments and {Count: 2}) {
        (owner, repo) = SplitOwnerAndRepo(arguments[1].Value);
    }
    else {
        (owner, repo) = SplitOwnerAndRepo(argument.Value);
    }
    if (owner is not { Length: > 0 } && repo is not { Length: > 0 }) {
        await Bot.ReplyAsync($"Please specify an owner/repo (use `{Bot} help {Bot.SkillName}` for more details) or set a default repo for this room with `{Bot} repo {{owner}}/{{repo}}`");
        return (null, null);
    }
    return (owner, repo);
}

static (string, string) SplitOwnerAndRepo(string ownerAndRepo) {
    var arry = ownerAndRepo.Split('/');
    
    if (arry.Length != 2) {
        return (null, null);
    }
    return (arry[0], arry[1]);
}

async Task ReplyWithUsage() {
    var usage = $@"`{Bot} help {Bot.SkillName}` to get help using this skill.";
    await Bot.ReplyAsync(usage);
}
