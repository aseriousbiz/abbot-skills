#load ".meta/globals.csx" // This is required for Intellisense in VS Code, etc. DO NOT TOUCH THIS LINE!
using System.Net.Http;

const string ApiKey = "GrafanaKey";
const string HostKey = "host";
const string ApiEndpointKey = "endpoint";

const string DefaultApiEndpoint = "d-solo";

Uri HostUri;
Headers Headers;
string Endpoint;

// This section describes the chat interactions
var (cmd, args) = Bot.Arguments.Pop();

Task reply = cmd switch
{
    "help" => ReplyWithHelp(args),
    var x when x =="db" || x == "dash" => args.Count > 0 ? DbCmd(args) : ListCmd(),
    "dash" => args.Count > 0 ? DbCmd(args) : ListCmd(),
    "panels" => PanelsCmd(args),
    "list" => ListCmd(),
    "config" => ConfigCmd(args),
    _ => ReplyWithHelp(args)
};

await reply;

Task ReplyWithHelp(IArguments args) {
    var (cmd, _) = args.Pop();
    return Bot.ReplyAsync(GetHelpMessage(cmd));
}

string GetHelpMessage(string cmd) {
    var reply = cmd switch {
        var x when x =="db" || x == "dash" => new []{
            $"• `{Bot.SkillName} {x} [dashboard]:[panel name or id] [from] [to]` - Render a given panel dashboard, with optional time ranges in hours, days, months, etc",
            $"• `{Bot.SkillName} {x} [dashboard]` - Render all panels in the dashboard",
            $"• `{Bot.SkillName} {x}` - List all dashboards",
            "Dashboard names can be partial. If it contains spaces, double-quote it.",
            "",
            "Examples:",
            $"• `{Bot.SkillName} {x} stats:logins 2d 8h` - Render the logins panel of the stats dashboard with a window of 2 days to 8 hours",
            $"• `{Bot.SkillName} {x} \"Global Stats:4\" 8h` - Render the panel with ID 4 of the Global Stats (stats) dashboard with a window of 8 hours from now",
            "",
            $"List panel IDs with `{Bot} {Bot.SkillName} panels [dashboard]`",
        },
        "panels" => new [] {
            "• `{Bot.SkillName} panels {dashboard}` - List the panels of a given dashboard.",
            $"Use the IDs returned by this in `{Bot.SkillName} db [dashboard]:[panel id]` calls as the panel id."
        },
        "config" => new [] {
            $"• `{Bot.SkillName} config` - List all configuration variables",
            $"• `{Bot.SkillName} config {HostKey}` - Get the value of {HostKey}",
            $"• `{Bot.SkillName} config {HostKey} {{value}}` - Set the value of {HostKey} to {{value}}",
            $"• `{Bot.SkillName} config {ApiEndpointKey}` - Get the value of {ApiEndpointKey}",
            $"• `{Bot.SkillName} config {ApiEndpointKey} {{value}}` - Set the value of {ApiEndpointKey} to {{value}}",
        },
        _ => new []{
            "• `{Bot.SkillName} db` or `dash` - Render panels in a dashboard.",
            "• `{Bot.SkillName} panels` - List the panels of a given dashboard. Use the IDs returned by this in the `db`/`dash` command as the panel id.",
            "• `{Bot.SkillName} list` - List available dashboards.",
            "• `{Bot.SkillName} config` - Manage configuration variables.",
            "• `{Bot.SkillName} help [command]` - Get detailed help on each available command.",
            "",
            "This skill requires a Grafana API key set up as a secret, and the hostname of your grafana instance.",
            "",
            "Grafana API Key:",
            "• Visit {Grafana Host}/org/apikeys to create a key (for eg https://play.grafana.com/org/apikeys)",
            $"• Visit {Bot.SkillUrl} and click \"Manage skill secrets\" to add a secret named `{ApiKey}` with the key you created.",
            "",
            "Grafana Host:",
            $"• Call `{Bot} {Bot.SkillName} config host [hostname]`, where `[hostname]` is your grafana instance url (for eg. https://play.grafana.com).",
            "",
            "Run `{Bot} {Bot.SkillName} help [command]` for more information on a command.",
        },
    };
    
    var sb = new System.Text.StringBuilder();
    foreach (var line in reply)
        sb.AppendLine(line);
    
    return sb.ToString();
}

Task ReplyWithLackOfUnderstanding() =>
    Bot.ReplyAsync($"Sorry, I did not understand that. {GetHelpMessage()}");

string GetHelpMessage() => $"`{Bot} help {Bot.SkillName}` to learn how to use this skill.";

async Task DbCmd(IArguments args)
{
    var (dashboard, vars) = args.Pop();
    string panelName = null;
    int panelId = -1;
    bool havePanelId = false;
    var parts = dashboard.Split(':');
    var selectPanel = parts.Length > 1;
    
    if (selectPanel)
    {
        dashboard = parts[0];
        panelName = parts[1];
        havePanelId = int.TryParse(panelName, out panelId);
    }
    
    var db = await GetDashboard(dashboard);
    if (db == null) {
        await Bot.ReplyAsync($"Dashboard {dashboard} not found.");
        return;
    }

    var pos = 0;
    var query = string.Join('&', vars.Select(x =>
    {
        if (x.Value.IndexOf('=') < 0)
        {
            if (++pos == 1)
                return $"from=now-{x.Value}";
            else if (pos == 2)
                return $"to=now-{x.Value}";
        }
        return x.Value;
    }));

    var panelFound = false;
    
    foreach (var availablePanel in await GetPanels(db.Uid))
    {
        if ((selectPanel && 
             ((havePanelId && panelId == availablePanel.Id) || (availablePanel.Title?.ToUpper() == panelName.ToUpper())))
            || !selectPanel)
        {
            panelFound = true;
            await RenderPanel(db, availablePanel, query);
        }
    }
    
    if (!panelFound) {
        await Bot.ReplyAsync($"No panel \"{panelName}\" found on dashboard \"{dashboard}\"");
    }
}

async Task ConfigCmd(IArguments arguments)
{
    var (cmd, args) = arguments.Pop();

    var result = cmd switch
    {
        HostKey => GetSetVariable(cmd, args), // host
        ApiEndpointKey => GetSetVariable(cmd, args), // endpoint
        _ => GetAllVariables(),
    };
    
    var reply = await result;
    await Bot.ReplyAsync(reply);
}

async Task ListCmd() {
    var sb = new System.Text.StringBuilder();
    sb.AppendLine("Available dashboards:");
    foreach (var dashboard in await GetDashboards()) {
        sb.AppendLine($"• <{HostUri}{dashboard.Url}|{dashboard.Title}>");
    }
    sb.AppendLine();
    sb.AppendLine("Dashboard Help:");
    sb.Append(GetHelpMessage("db"));
    await Bot.ReplyAsync(sb.ToString());
}

async Task PanelsCmd(IArguments args) {

    var sb = new System.Text.StringBuilder();
    var (dashboard, _) = args;
    if (dashboard is IMissingArgument) {
        sb.AppendLine("Which dashboard do you want to list panels for?");
        foreach (var d in await GetDashboards()) {
            sb.AppendLine($"• {d}");
        }
        sb.AppendLine("");
        sb.AppendLine($"Call me with `{Bot} {Bot.SkillName} panels [dashboard]`. `[dashboard]` can be a partial name.");
        await Bot.ReplyAsync(sb.ToString());
        return;
    }


    var db = await GetDashboard(dashboard.Value);
    if (db == null) {
        sb.AppendLine($"Dashboard {dashboard.Value} not found.");
        sb.AppendLine("Available dashboards:");
        foreach (var d in await GetDashboards()) {
            sb.AppendLine($"• {d}");
        }
        await Bot.ReplyAsync(sb.ToString());
        return;
    }

    var panels = await GetPanels(db.Uid);

    sb.AppendLine($"Available panels for dashboard {dashboard.Value}:");
    sb.AppendLine($"\tId\tPanel");
    sb.AppendLine($"\t--\t--------");
    foreach (var panel in await GetPanels(db.Uid)) {
        sb.AppendLine($"\t{panel.Id}\t{panel.Title}");
    }
    await Bot.ReplyAsync(sb.ToString());
}

async Task<string> GetAllVariables()
{
    var sb = new System.Text.StringBuilder();
    sb.AppendLine(await GetSetVariable(HostKey));
    sb.AppendLine(await GetSetVariable(ApiEndpointKey));
    return sb.ToString();
}

async Task<string> GetSetVariable(string key, IArguments args = null)
{
    if (args?.Count > 0) {
        var (val, _) = args.Pop();
        val = val?.TrimEnd('/');
        await Bot.Brain.WriteAsync(key, val);
        return $"{key} is set to '{val}'";
    } else {
        var val = await Bot.Brain.GetAsync(key);
        if (string.IsNullOrWhiteSpace(val) && key == ApiEndpointKey) {
            val = DefaultApiEndpoint;
            return $"{key} is set to '{val}' (default value)";
        }
        if (string.IsNullOrWhiteSpace(val))
            return $"{key} is not set";
        return $"{key} is set to '{val}'";
    }
}

async Task RenderPanel(Dashboard db, PanelData panel, string query) {
    var (success, errorMsg) = await EnsureAPI();
    if (!success) {
        await Bot.ReplyAsync(errorMsg);
        return;
    }
    
    var url = $"{HostUri}render/{Endpoint}/{db.Uid}/{db.Name}?panelId={panel.Id}&{query}";

    var http = new HttpClient();
    using var request = new HttpRequestMessage
    {
        Method = HttpMethod.Get,
        RequestUri = new Uri(url)
    };

    Headers.CopyTo(request.Headers);
    
    var response = await http.SendAsync(request);
    response.EnsureSuccessStatusCode();

    var image = await response.Content.ReadAsByteArrayAsync();
    var base64 = System.Convert.ToBase64String(image);
    
    var link = new Uri($"{HostUri}{db.Url}/?panelId={panel.Id}&fullscreen&{query}");
    await Bot.ReplyWithImageAsync(base64, title: panel.Title, titleUrl: link);
}


async Task<List<Dashboard>> GetDashboards() {
    var (success, errorMsg) = await EnsureAPI();
    var result = new List<Dashboard>();
    if (!success) {
        await Bot.ReplyAsync(errorMsg);
        return result;
    }
    
    var query = $"{HostUri}api/search?type=dash-db";

    dynamic response = await Bot.Http.GetJsonAsync(query, Headers);
    foreach (var r in response) {
        if (r.type != "dash-db")
            continue;
        result.Add(new Dashboard((string)r.uid, (string)r.title, (string)r.url));
    }
    return result;
}

async Task<Dashboard> GetDashboard(string dashboard) {
    var (success, errorMsg) = await EnsureAPI();
    if (!success) {
        await Bot.ReplyAsync(errorMsg);
        return null;
    }
    
    var query = $"{HostUri}api/search?query={dashboard}";

    dynamic response = await Bot.Http.GetJsonAsync(query, Headers);
    
    if (response.Count > 0) {
        return new Dashboard((string)response[0].uid, (string)response[0].title, (string)response[0].url);
    }
    return null;
}

async Task<List<PanelData>> GetPanels(string uid) {
    var (success, errorMsg) = await EnsureAPI();
    var results = new List<PanelData>();
    if (!success) {
        await Bot.ReplyAsync(errorMsg);
        return results;
    }
    
    var query = $"{HostUri}api/dashboards/uid/{uid}";

    dynamic response = await Bot.Http.GetJsonAsync(query, Headers);
    foreach (var panel in response.dashboard.panels) {
        results.Add(new PanelData((string)panel.title, (string)panel.id));
    }
    return results;
}

async Task<(bool, string)> EnsureAPI()
{
    if (HostUri != null && Headers != null)
        return (true, null);
    
    string host = await Bot.Brain.GetAsync(HostKey);
    if (!Uri.TryCreate(host, UriKind.Absolute, out var uri))
    {
        // tell the user to set things
        return (false, $"I need to know what your Grafana host is. Call `{Bot} {Bot.SkillName} config {HostKey} {{host}}`, where {{host}} is your grafana instance url (for eg. https://play.grafana.org).");
    }

    Endpoint = await Bot.Brain.GetAsync(ApiEndpointKey);
    if (string.IsNullOrWhiteSpace(Endpoint)) {
        Endpoint = DefaultApiEndpoint;
    }

    HostUri = uri;

    // special casing the example dashboards that don't need auth
    if (host == "https://play.grafana.org")
    {
        Headers = new Headers();
    }
    else
    {
        var key = await Bot.Secrets.GetAsync(ApiKey);
        if (string.IsNullOrWhiteSpace(key))
        {
            // tell the user to set things
            return (false,
                "This skill requires a Grafana API key set up as a secret. "
                + "Visit {host}/org/apikeys to create a key. "
                + $"Then visit {Bot.SkillUrl} and click \"Manage skill secrets\" to add a secret named `{ApiKey}` with the key you created.");
        }

        Headers = new Headers(new Dictionary<string, string> {{"Authorization", $"Bearer {key}"}});
    }

    return (true, null);
}

public class Dashboard {
    
    public Dashboard(string uid, string title, string url) {
        Title = title;
        Url = url.TrimStart('/');
        Uid = uid;
        var idx = Url.LastIndexOf('/');
        Name = Url[(idx < 0 ? 0 : idx + 1)..];
    }
    
    public string Uid { get; }
    public string Url { get; }
    public string Name { get; }
    public string Title { get; }
}

public class PanelData {
    public PanelData(string title, string id) {
        Title = title;
        Id = int.Parse(id);
    }
    
    public int Id { get; }
    public string Title { get; }
}

