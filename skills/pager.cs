/* 
PagerDuty Skill for triggering pager duty incidents and responding to them.

TODO:
- [ ] Implement ability to restrict skill to a set list of schedules (need to understand how and why that's used).
- [ ] Implement ability to restrict skill to a set list of PagerDuty services (need to understand how and why that's used).

For example, do people want to restrict schedules/services per chat room?
*/

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

Task task = Bot.Arguments switch {
    ({Value: "ack!"} or {Value: "acknowledge!"}, IMissingArgument, _) => AcknowledgeAllIncidentsAsync(),
    ({Value: "ack"} or {Value: "acknowledge"}, IMissingArgument, _) => AcknowledgeAssignedIncidentsAsync(), // Skip the "ack" arg and pass the rest.
    ({Value: "ack"} or {Value: "acknowledge"}, _, _) => AcknowledgeIncidentsAsync(Bot.Arguments.Skip(1)), // Skip the "ack" arg and pass the rest.
    ({Value: "am"}, {Value: "I"} or {Value: "i"}, {Value: "on call"} or {Value: "on call?"}) => AmIOnCallAsync(),
    ({Value: "bot"}, _) => SetPagerDutyBotConfigAsync(Bot.Arguments.Skip(1)),
    ({Value: "forget"}, {Value: "me"}, IMissingArgument) => ForgetPagerDutyEmail(),
    ({Value: "incident"}, IArgument id, _) => ReplyWithIncidentAsync(id),
    (IMissingArgument, _, _) or ({Value: "me"}, IMissingArgument, _) => ReplyWithPagerUserInfo(Bot.From),
    ({Value: "me"}, {Value: "as"}, var emailArg) => SetPagerDutyEmail(emailArg),
    ({Value: "notes"}, IArgument id, _) => ReplyWithIncidentNotesAsync(id),
    ({Value: "services"}, IMissingArgument, IMissingArgument) => ListServicesAsync(),
    ({Value: "sup"}, _, _) or ({Value: "inc"}, _, _) or ({Value: "incidents"}, _, _) or ({Value: "problems"}, _, _) => ReplyWithIncidentsAsync(),
    ({Value: "trigger"} or {Value: "page"}, _, _) => TriggerPagerDutyAsync(Bot.Arguments.Skip(1)), // Skip the "trigger" arg and pass the rest.
    ({Value: "who's"}, {Value: "on"}, {Value: "call"} or {Value: "call?"}, _) => HandleWhoIsOnCallAsync(Bot.Arguments.Skip(3)),
    ({Value: "who"}, {Value: "is"}, {Value: "on"}, {Value: "call"} or {Value: "call?"}, _)  => HandleWhoIsOnCallAsync(Bot.Arguments.Skip(4)),
    _ => ReplyWithHelpAsync()
};

await task;

// Perform the actual pagerduty actions
async Task TriggerPagerDutyAsync(IArguments args) {
    var botUserEmail = await EnsureBotUserEmailSetAsync();
    if (botUserEmail is null) {
        return; // The EnsureBotUser email will have replied with an appropriate message.
    }

    Task action = args switch {
        (IMentionArgument mention, var msg) => PageUserAsync(mention.Mentioned, msg.Value),
        (IArgument target, var msg) => PageTargetAsync(target.Value, msg.Value),
        _ => Bot.ReplyAsync("???")
    };

    await action;
}

async Task PageUserAsync(IChatUser chatUser, string message) {
    var pagerUser = await GetPagerDutyUser(chatUser);
    if (pagerUser?.User is null) {
        return;
    }
    var response = await CreateIncidentAsync(serviceApiKey, message, chatUser);
    var incident = response?.incident;
    if (incident is null) {
        await Bot.ReplyAsync("Something went wrong trying to create the incident.");
        return;
    }
    await Bot.ReplyAsync($":pager: triggered! Incident #{incident.incident_number} assigned to {chatUser}");
}

async Task PageTargetAsync(string target, string message) {
    var policy = await GetEscalationPolicyAsync(target);
    if (policy is not null) {
        // Page the policy.
        return;
    }
    var schedules = await GetSchedules(target);
    if (schedules is {Count: > 0}) {
        var schedule = schedules.First();
        var oncall = await GetCurrentOnCallUser(schedule.id);
        if (oncall is null) {
            await Bot.ReplyAsync($"I do not know the on call user for the schedule `{target}`");
            return;
        }
        return;
    }

    await Bot.ReplyAsync($"Paging ...");
}

async Task AcknowledgeAllIncidentsAsync() {
    var pagerDutyUser = await GetPagerDutyUser(Bot.From);
    if (pagerDutyUser?.PagerDutyEmail is null) {
        return;
    }
    
    var assigned = await GetIncidentsAsync(IncidentStatus.Triggered, IncidentStatus.Acknowledged);
    if (assigned is {Count: 0}) {
        await Bot.ReplyAsync("No incidents to acknowledge.");
        return;
    }
    
    await UpdateIncidentsAsync(assigned, IncidentStatus.Acknowledged, pagerDutyUser);
}

async Task AcknowledgeAssignedIncidentsAsync() {
    var pagerDutyUser = await GetPagerDutyUser(Bot.From);
    if (pagerDutyUser?.PagerDutyEmail is null) {
        return;
    }
    
    var assigned = await GetAssignedIncidentsAsync(pagerDutyUser);
    if (assigned is {Count: 0}) {
        await Bot.ReplyAsync("No incidents assigned to you.");
        return;
    }
    
    await UpdateIncidentsAsync(assigned, IncidentStatus.Acknowledged, pagerDutyUser);
}

async Task AcknowledgeIncidentsAsync(IArguments arguments) {
    var incidentNumbers = arguments.Select(arg => arg.ToInt32()).ToList();
    if (incidentNumbers.Any(n => n is null)) {
        await Bot.ReplyAsync("Some of those incident numbers don't seem to be incident numbers");
        return;
    }
    
    // The reason "Acknowledged" are included in the status filter is to allow a different user 
    // to take ownership of an acknowledged incident by using the "ack" sub-command.
    await UpdateIncidentsByNumbersAsync(incidentNumbers.Select(num => num.Value), new[] {IncidentStatus.Triggered, IncidentStatus.Acknowledged }, IncidentStatus.Acknowledged);
}

// Update the specified incidents
async Task UpdateIncidentsByNumbersAsync(IEnumerable<int> incidentNumbers, IEnumerable<IncidentStatus> statuses, IncidentStatus updatedStatus) {
    var pagerDutyUser = await GetPagerDutyUser(Bot.From);
    if (pagerDutyUser?.User is null) {
        return;
    }
    var ids = incidentNumbers.ToList();
    var incidents = await GetIncidentsAsync(statuses.ToArray());
    var foundIncidents = incidents.Where(i => ids.Contains((int)i.incident_number)).ToList();
    var incidentNumbersText = string.Join(", ", incidentNumbers);
    if (foundIncidents is {Count: 0}) {
        await Bot.ReplyAsync($"Couldn't find incident(s) {incidentNumbersText}. Use `{Bot} pager incidents` for listing.");
        return;
    }
    
    await UpdateIncidentsAsync(foundIncidents, updatedStatus, pagerDutyUser);
}

async Task UpdateIncidentsAsync(IEnumerable<dynamic> incidents, IncidentStatus updatedStatus, PagerDutyUser pagerDutyUser) {
    var data = new {
        incidents = incidents.Select(i => new {
            id = i.id,
            type = "incident_reference",
            status = updatedStatus.ToString().ToLowerInvariant()
        })
    };
    var incidentNumbers = string.Join(", ", incidents.Select(i => i.incident_number));
    var response = await CallPagerDutyApiAsync("/incidents", HttpMethod.Put, pagerDutyUser, data);
    List<dynamic> updatedIncidents = response?.incidents?.ToObject<List<dynamic>>();
    if (updatedIncidents is null) {
        await Bot.ReplyAsync($"Problem updating incidents {incidentNumbers}");
        return;
    }
    
    var updatedIncidentNumbers = string.Join(", ", updatedIncidents.Select(i => i.incident_number));
    var pluralSuffix = updatedIncidents.Count > 1 ? "s" : string.Empty;
    await Bot.ReplyAsync($"Incident{pluralSuffix} {updatedIncidentNumbers} {updatedStatus.ToString().ToLowerInvariant()}");
}

Task HandleWhoIsOnCallAsync(IArguments arguments) {
    return arguments switch {
        ({Value: "for"}, var scheduleArg) => WhoIsOnCallAsync(scheduleArg.Value),
        (IMissingArgument, _) => WhoIsOnCallAsync(null),
        _ => ReplyWithHelpAsync()
    };
}

async Task<dynamic> GetCurrentOnCallUser(dynamic scheduleId) {
    var now = DateTimeOffset.UtcNow;
    var oneHour = now.AddHours(1);
    var endpoint = $"/schedules/{scheduleId}/users?since={now:o}&until={oneHour:o}";
    try {
        var response = await CallPagerDutyApiAsync(endpoint);
        if (response?.users?.Count > 0) {
            return response.users[0];
        }
    }
    catch (HttpRequestException e) {
        await Bot.ReplyAsync($"Error requesting {endpoint}\n" + e.Message);
    }
    
    return null;
}

async Task WhoIsOnCallAsync(string scheduleName) {
    var schedules = await GetSchedules(scheduleName);
    if (schedules.Count is 0) {
        var message = scheduleName is {Length: > 0}
            ? $"I could not find a schedule named `{scheduleName}`"
            : "No schedules found!";
        await Bot.ReplyAsync(message);
        return;
    }
    var messages = new List<string>();
    foreach (var schedule in schedules) {
        var user = await GetCurrentOnCallUser(schedule.id);
        if (user is not null) {
            messages.Add($"{user.name} is on call for {schedule.name} - {schedule.html_url}");
        }
    }

    if (messages.Count is 0) {
        await Bot.ReplyAsync("No users are on call!");
        return;
    }
    await Bot.ReplyAsync(messages.ToMarkdownList());
}

async Task AmIOnCallAsync() {
    var user = await GetPagerDutyUser(Bot.From);
    var userId = user.User?.id;
    
    if (userId is null) {
        await Bot.ReplyAsync($"Couldn't figure out the PagerDuty user connected to your account. `{Bot} {Bot.SkillName} me as youremail@yourdomain.com` to set your PagerDuty email.");
        return;
    }
    var schedules = await GetSchedules();
    if (schedules.Count is 0) {
        await Bot.ReplyAsync("No schedules found!");
        return;
    }
    
    var responses = new List<string>();
    foreach (var schedule in schedules) {
        string scheduleId = schedule.id;
        var oncallUser = await GetCurrentOnCallUser(scheduleId);
        if (userId == oncallUser?.id) {
            responses.Add($"Yes, you are on call for {schedule.name} - {schedule.html_url}");
        }
        else if (oncallUser?.name is null) {
            responses.Add($"No, you are NOT on call for {schedule.name} - {schedule.html_url}");
        }
        else {
            responses.Add($"No, you are NOT on call for {schedule.name} (but {oncallUser.name} is) - {schedule.html_url}");
        }
    }
    await Bot.ReplyAsync(responses.ToMarkdownList());
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
    var incidents = await GetIncidentsAsync(IncidentStatus.Triggered, IncidentStatus.Acknowledged);
    
    var triggered = incidents.Where(i => i.status == "triggered").Select(i => FormatIncident(i)).Cast<string>();
    var acknowledged = incidents.Where(i => i.status == "acknowledged").Select(i => FormatIncident(i)).Cast<string>();
    
    var response = incidents is {Count: 0}
        ? "No open incidents"
        : $"Triggered:\n----------\n{triggered.ToMarkdownList()}\nAcknowledged:\n-------------\n{acknowledged.ToMarkdownList()}";
    await Bot.ReplyAsync(response);
}

async Task<List<dynamic>> GetIncidentsAsync(params IncidentStatus[] statuses) {
    var endpoint = "/incidents?sort_by=incident_number:asc"
        + statuses.Aggregate(string.Empty, (accumulate, status) => $"&statuses[]={status.ToString().ToLowerInvariant()}{accumulate}");
    var result = await CallPagerDutyApiAsync(endpoint);
    return result.incidents.ToObject<List<dynamic>>();
}

async Task<List<dynamic>> GetAssignedIncidentsAsync(PagerDutyUser pagerDutyUser) {
    // When user_ids[] are specified, only triggered and acknowledged are returned because resolved are not assigned to anyone.
    var endpoint = $"/incidents?sort_by=incident_number:asc&user_ids[]={pagerDutyUser.User.id}";
    var result = await CallPagerDutyApiAsync(endpoint);
    return result.incidents.ToObject<List<dynamic>>();
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
        notes.Add($"{note.created_at} {note.user.summary}: {note.content}");
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
    await Bot.ReplyAsync($"Okay, I’ll remember your PagerDuty email is {email}");
}

async Task ForgetPagerDutyEmail() {
    await WritePagerDutyEmail(Bot.From, null);
    await Bot.ReplyAsync("Ok, I’ve forgotten your PagerDuty email");
}

Task<string> GetPagerDutyEmail(IChatUser user) {
    return Bot.Brain.GetAsAsync<string>($"{user.Id}|PagerDutyEmail");
}

async Task ReplyWithPagerUserInfo(IChatUser user) {
    var pagerUser = await GetPagerDutyUser(user);
    
    var emailNote = pagerUser switch {
            {PagerDutyEmail: {Length: > 0}} => $"You’ve told me your PagerDuty email is {pagerUser.PagerDutyEmail}",
            {DefaultEmail: {Length: > 0}} => $"I’m assuming your PagerDuty email is {pagerUser.DefaultEmail}. Change it with `{Bot} {Bot.SkillName} me as you@yourdomain.com`",
            _ => $"I don't know your email. Either set it with `{Bot} my email is your@yourdomain.com` or set it specifically for this skill with `{Bot} {Bot.SkillName} me as you@yourdomain.com`"
    };
    
    var response = pagerUser.User is not null
        ? $"I found your PagerDuty user {pagerUser.User.html_url}, {emailNote}"
        : $"I couldn't find your user :( {emailNote}";
    
    await Bot.ReplyAsync(response);
}

async Task<PagerDutyUser> GetPagerDutyUser(IChatUser user) {
    var pagerDutyUser = new PagerDutyUser {
        PagerDutyEmail = await GetPagerDutyEmail(user),
        DefaultEmail = user.Email
    };
    var email = pagerDutyUser.PagerDutyEmail ?? pagerDutyUser.DefaultEmail;
    
    var (possessive, addressee) = user.Id.Equals(Bot.From.Id, StringComparison.Ordinal)
        ? ("your", "you")
        : ($"{user.Name}'s", user.Name);
    
    if (email is null) {
        await Bot.ReplyAsync($"Sorry, I can’t figure out {possessive} email address :( Can {addressee} tell me with `{Bot} pager me as you@yourdomain.com`?");
        return pagerDutyUser;
    }
    var encodedEmail = Uri.EscapeDataString(email);
    dynamic response = await CallPagerDutyApiAsync($"/users?query={encodedEmail}");

    var count = response?.users?.Count ?? 0;
    if (count is 1) {
        pagerDutyUser.User = response.users[0];
        return pagerDutyUser;
    }
    await Bot.ReplyAsync($"Sorry, I expected to get 1 user back for {email}, but got {count} :sweat:. If your PagerDuty email is not {email} use `{Bot} pager me as {{email}}`");
    return pagerDutyUser;
}

Task WritePagerDutyEmail(IChatUser user, string email) {
    var key = $"{user.Id}|PagerDutyEmail";
    if (email is null) {
        return Bot.Brain.DeleteAsync(key);
    }
    return Bot.Brain.WriteAsync(key, email);
}

async Task<dynamic> CallPagerDutyApiAsync(string path, HttpMethod method = null, PagerDutyUser fromUser = null, object data = null) {
    var headers = new Headers {
        { "Authorization", $"Token token={restApiKey}" }
    };
    if (fromUser?.PagerDutyEmail is not null) {
        headers["From"] = fromUser.PagerDutyEmail;
    }
    var endpoint = new Uri($"https://api.pagerduty.com{path}");
    return await Bot.Http.SendJsonAsync(endpoint, method ?? HttpMethod.Get, data, headers);
}

async Task<dynamic> CreateIncidentAsync(string serviceId, string message, IChatUser assignee = null, dynamic escalationPolicy = null) {
    var pagerDutyUser = await GetPagerDutyUser(Bot.From);
    if (pagerDutyUser is null) {
        return null;
    }
    var incident = new PagerDutyIncident {
        service = new PagerDutyService { id = serviceId },
        title = message
    };
    if (assignee is not null) {
        var asigneeUser = await GetPagerDutyUser(assignee);
        if (asigneeUser?.User is null) {
            return null;
        }
        incident.assignments = new[] {
            new {
                assignee = new {
                    asigneeUser.User.id,
                    type = "user_reference"
                }
            }
        };
    }
    else if (escalationPolicy is not null) {
        incident.escalation_policy = new {
            escalationPolicy.id,
            type = "escalation_policy_reference"
        };
    }
    var data = new { incident };
    return await CallPagerDutyApiAsync("/incidents", HttpMethod.Post, pagerDutyUser, data);
}

async Task<List<dynamic>> GetSchedules(string scheduleName = null) {
    var endpoint = scheduleName is not null
            ? $"/schedules?query={Uri.EscapeDataString(scheduleName)}"
            : $"/schedules";
    var response = await CallPagerDutyApiAsync(endpoint);
    return response?.schedules?.ToObject<List<dynamic>>();
}

async Task ListServicesAsync() {
    dynamic response = await CallPagerDutyApiAsync("/services");
    List<dynamic> services = response?.services?.ToObject<List<dynamic>>();
    if (services is null) {
        await Bot.ReplyAsync("Something went wrong calling the PagerDuty API.");
        return;
    }
    if (services.Count is 0) {
        await Bot.ReplyAsync("No services found!");
        return;
    }
    var replies = services.Select(service => $"{service.id}: {service.name} ({service.status}) - {service.html_url}").ToMarkdownList();
    await Bot.ReplyAsync(replies);
}

async Task<dynamic> GetEscalationPolicyAsync(string query) {
    dynamic response = await CallPagerDutyApiAsync($"/escalation_policies?query={Uri.EscapeDataString(query)}");
    List<dynamic> policies = response?.policies?.ToObject<List<dynamic>>();
    if (policies is null) {
        return null;
    }
    
    if (policies.Count is 1) {
        return policies[0];
    }

    // Try exact match, but only when there is a single exact match.
    var exactMatch = policies.Select(p => query.Equals(p.name, StringComparison.OrdinalIgnoreCase)).SingleOrDefault();
    if (exactMatch is not null) {
        return exactMatch;
    }
    
    return null;
}

public class PagerDutyUser {
    public string PagerDutyEmail { get; set; }
    public string DefaultEmail { get; set; }
    public dynamic User { get; set; }
}

public class PagerDutyIncident {
    public string type => "incident";
    public string title { get; set; }
    public PagerDutyService service { get; set; }
    public object assignments {get; set;}
    public object escalation_policy {get; set;}
}

public class PagerDutyService {
    public string id {get; set;}
    public string type => "service_reference";
}

public enum IncidentStatus {
    Acknowledged,
    Triggered
}
