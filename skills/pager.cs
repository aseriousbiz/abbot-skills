/* PagerDuty Skill for triggering pager duty incidents and responding to them. */

var restApiKey = await Bot.Secrets.GetAsync("pagerduty-api-key");
if (restApiKey is not {Length: > 0}) {
    await Bot.ReplyAsync("Please set your PagerDuty Rest API key in a secret named `pagerduty-api-key`.\nSee https://support.pagerduty.com/docs/generating-api-keys#section-generating-an-api-key for more information about this API key.");
    return;
}

var serviceApiKey = await Bot.Secrets.GetAsync("pagerduty-service-api-key");
if (serviceApiKey is not {Length: > 0}) {
    await Bot.ReplyAsync("Please set your PagerDuty Incident Service API key in a secret named `pagerduty-service-api-key`.\nThis should be assigned to a dummy escalation policy that doesn't actually notify, as Abbot will trigger on this before reassigning it.\nSee https://developer.pagerduty.com/docs/rest-api-v2/incident-creation-api/ for more information about this API key.");
    return;
}

// Set up pre-requisites.
Task task = Bot.Arguments switch {
    ({Value: "forget"}, {Value: "me"}, IMissingArgument) => ForgetPagerDutyEmail(),
    ({Value: "me"}, IMissingArgument, IMissingArgument) => GetPagerDutyUser(Bot.From),
    ({Value: "me"}, {Value: "as"}, var emailArg) => SetPagerDutyEmail(emailArg),
    ({Value: "bot"}, _) => SetPagerDutyBotConfigAsync(Bot.Arguments.Skip(1)),
    ({Value: "trigger"}, _, _) => TriggerPagerDutyAsync(Bot.Arguments.Skip(1)), // Skip the "trigger" arg and pass the rest.
    ({Value: "incident"}, IArgument id, _) => ReplyWithIncidentAsync(id),
    ({Value: "notes"}, IArgument id, _) => ReplyWithIncidentNotesAsync(id),
    ({Value: "sup"}, _, _) or ({Value: "inc"}, _, _) or ({Value: "incidents"}, _, _) or ({Value: "problems"}, _, _) => ReplyWithIncidentsAsync(),
    _ => ReplyWithHelpAsync()
};

await task;

// Perform the actual pagerduty actions
async Task TriggerPagerDutyAsync(IArguments args) {
    var botUserEmail = await EnsureBotUserEmailSetAsync();
    if (botUserEmail is null) {
        return; // The EnsureBotUser email will have replied with an appropriate message.
    }
    await Bot.ReplyAsync("Ring ring!");
}

async Task SetPagerDutyBotConfigAsync(IArguments arguments) {
    Task task = arguments switch {
        (IMissingArgument, _, _) => ReportBotUserEmailAsync(),
        ({Value: "id"}, IArgument idArg, IMissingArgument) => SetPagerDutyBotIdAsync(idArg),
        ({Value: "id"}, {Value: "is"}, IArgument idArg) => SetPagerDutyBotIdAsync(idArg),
        (var emailArg, IMissingArgument, _) => SetPagerDutyBotEmailAsync(emailArg),
        ({Value: "is"}, var emailArg, _) => SetPagerDutyBotEmailAsync(emailArg),
        _ => ReplyWithHelpAsync()
    };
    await task;
}

async Task SetPagerDutyBotIdAsync(IArgument emailArg) {
    var botEmail = emailArg.Value;
    if (!botEmail.Contains('@')) {
        await Bot.ReplyAsync("Please specify a valid email address for the PagerDuty bot user. For example, `{Bot} {Bot.SkillName} bot is {{email}}`.");
        return;
    }
    await Bot.Brain.WriteAsync("PAGERDUTY_FROM_EMAIL", botEmail);
    await Bot.ReplyAsync($"Great! I've set the default PagerDuty bot email to `{botEmail}`.");
}

async Task ReportBotUserEmailAsync() {
    var botUserEmail = await GetDefaultUserEmail();
    if (botUserEmail is not {Length: > 0}) {
        await Bot.ReplyAsync("Please specify the email address for the PagerDuty bot user. For example, `{Bot} {Bot.SkillName} bot is {{email}}`.");
        return;
    }
    await Bot.ReplyAsync($"`{botUserEmail}`");
    return;
}

async Task SetPagerDutyBotEmailAsync(IArgument emailArg) {
    var botEmail = emailArg.Value;
    if (!botEmail.Contains('@')) {
        await Bot.ReplyAsync("Please specify a valid email address for the PagerDuty bot user. For example, `{Bot} {Bot.SkillName} bot is {{email}}`.");
        return;
    }
    await Bot.Brain.WriteAsync("PAGERDUTY_FROM_EMAIL", botEmail);
    await Bot.ReplyAsync($"Great! I've set the default PagerDuty bot email to `{botEmail}`.");
}

async Task ReplyWithHelpAsync() {
    var botUserEmail = await EnsureBotUserEmailSetAsync();
    if (botUserEmail is null) {
        return; // We've warned the user about setting up the bot.
    }
    await Bot.ReplyAsync($"Sorry, I did not understand that. Try `{Bot} help {Bot.SkillName}` to learn how to use the {Bot.SkillName} skill.");
}

// This would be the email address for a bot user in PagerDuty.
Task<string> GetDefaultUserEmail() {
    return Bot.Brain.GetAsAsync<string>("PAGERDUTY_FROM_EMAIL");
}

Task WriteDefaultUserEmail(string email) {
    return Bot.Brain.WriteAsync("PAGERDUTY_FROM_EMAIL", email);
}

async Task<string> EnsureBotUserEmailSetAsync() {
    var botUserEmail = await GetDefaultUserEmail();
    if (botUserEmail is not {Length: > 0}) {
        await Bot.ReplyAsync($@"Please set the email of the default ""actor"" user for incident creation and modification. This would be the email address for a bot user in PagerDuty.
`{Bot} {Bot.SkillName} bot is {{email}}`");
        return null;
    }
    return botUserEmail;
}

async Task ReplyWithIncidentAsync(IArgument idArg) {
    var incidentNumber = idArg.ToInt32();
    if (incidentNumber is null) {
        await Bot.ReplyAsync("That doesn't look like an incident number to me.");
        return;
    }
    var result = await CallPagerDutyApiAsync($"/incidents/{incidentNumber.Value}");
    await Bot.ReplyAsync(FormatIncident(result.incident));
}

async Task ReplyWithIncidentsAsync() {
    var result = await CallPagerDutyApiAsync("/incidents?sort_by=incident_number:asc");
    var triggered = new List<string>();
    var acknowledged = new List<string>();
    foreach(dynamic incident in result.incidents) {
        var listToAdd = incident.status == "triggered" ? triggered : acknowledged;
        listToAdd.Add(FormatIncident(incident));
    }
    var response = triggered is {Count: 0} && acknowledged is {Count: 0}
        ? "No open incidents"
        : $"Triggered:\n----------\n{triggered.ToMarkdownList()}\nAcknowledged:\n-------------\n{acknowledged.ToMarkdownList()}";
    await Bot.ReplyAsync(response);
}

string FormatIncident(dynamic incident) {
    var summary = incident.title;
    var assignee = incident.assignments?[0]?.assignee?.summary;
    string assignedTo = assignee is not null
        ? $"- assigned to {assignee}"
        : string.Empty;
    return $"{incident.incident_number}: {incident.created_at} {summary} {assignedTo}";
}

async Task CreateIncidentNoteAsync(IArgument idArg, IArgument content) {
    var incidentNumber = idArg.ToInt32();
    if (incidentNumber is null) {
        await Bot.ReplyAsync("That doesn't look like an incident number to me.");
        return;
    }
    if (content is IMissingArgument) {
        await Bot.ReplyAsync("Please supply some content for the incident note.");
        return;
    }
}

async Task ReplyWithIncidentNotesAsync(IArgument idArg) {
    var incidentNumber = idArg.ToInt32();
    if (incidentNumber is null) {
        await Bot.ReplyAsync("That doesn't look like an incident number to me.");
        return;
    }
    var notes = new List<string>();
    var result = await CallPagerDutyApiAsync($"/incidents/{incidentNumber}/notes");
    foreach (var note in result.notes) {
        notes.Add($"{note.created_at} #{note.user.summary}: #{note.content}");
    }
    var response = notes is {Count: 0}
        ? $"Incident {incidentNumber} does not have any notes."
        : notes.ToMarkdownList();
    await Bot.ReplyAsync(response);
}

async Task SetPagerDutyEmail(IArgument emailArg) {
    if (emailArg is IMissingArgument) {
        await Bot.ReplyAsync("Please specify your pager email.");
        return;
    }
    var email = emailArg.Value;
    if (!email.Contains('@')) {
        await Bot.ReplyAsync("Please specify a valid email address for your pager email.");
        return;
    }
    await WritePagerDutyEmail(Bot.From, email);
    await Bot.ReplyAsync($"Okay, I'll remember your PagerDuty email is {email}");
}

async Task ForgetPagerDutyEmail() {
    await WritePagerDutyEmail(Bot.From, null);
    await Bot.ReplyAsync("Ok, I've forgotten your PagerDuty email");
}

Task<string> GetPagerDutyEmail(IChatUser user) {
    return Bot.Brain.GetAsAsync<string>($"{user.Id}|PagerDutyEmail");
}

async Task<dynamic> GetPagerDutyUser(IChatUser user) {
    var email = (await GetPagerDutyEmail(user)) ?? user.Email;
    var (possessive, addressee) = user.Id.Equals(Bot.From.Id, StringComparison.Ordinal)
        ? ("your", "you")
        : ($"{user.Name}'s", user.Name);
    
    if (email is null) {
        await Bot.ReplyAsync($"Sorry, I can't figure out {possessive} email address :( Can {addressee} tell me with `{Bot} pager me as you@yourdomain.com`?");
        return null;
    }
    var encodedEmail = Uri.EscapeDataString(email);
    await Bot.ReplyAsync($"/users?query={encodedEmail}");
    dynamic response = await CallPagerDutyApiAsync($"/users?query={encodedEmail}");

    var count = response?.users?.Count ?? 0;
    if (count is 1) {
        return response.users[0];
    }
    await Bot.ReplyAsync($"Sorry, I expected to get 1 user back for {email}, but got {count} :sweat:. If your PagerDuty email is not {email} use `{Bot} pager me as {{email}}`");
    return null;
}

Task WritePagerDutyEmail(IChatUser user, string email) {
    var key = $"{user.Id}|PagerDutyEmail";
    if (email is null) {
        return Bot.Brain.DeleteAsync(key);
    }
    return Bot.Brain.WriteAsync(key, email);
}

async Task<dynamic> CallPagerDutyApiAsync(string path, HttpMethod method = null) {
    var headers = new Headers {{ "Authorization", $"Token token={restApiKey}" }};
    var endpoint = new Uri($"https://api.pagerduty.com{path}");
    return await Bot.Http.SendJsonAsync(endpoint, method ?? HttpMethod.Get, null, headers);
}
