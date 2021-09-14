#load ".meta/globals.csx" // This is required for Intellisense in VS Code, etc. DO NOT TOUCH THIS LINE!
/* ------------------------------------------------------------------------------------------------
Description: Subscribe to Cloud Events webhook subscriptions (such as from Azure EventGrid) and report them to a chat room. 
This supports the Cloud Events WebHook Spec v1.0.1: https://github.com/cloudevents/spec/blob/v1.0.1/http-webhook.md

SEE BLOG POST AND VIDEO HERE: https://blog.ab.bot/archive/2021/03/04/abbot-cloud-events/

Usage: Run `@abbot attach cloud-event` in the chat room that should receive these events, then supply the trigger URL as the webhook URL to the event source.

The following commands work in chat:

* `@abbot cloud-event list` - List all the trigger URLs used as webhooks.
* `@abbot cloud-event list {triggerUrl}` - lists the allowed origins for the specified trigger URL. Instead of the URL, you can specify the number of the trigger URL in the list ex. `@abbot list 2`.
* `@abbot cloud-event remove {triggerUrl}` - removes all allowed origins for that trigger URL. No webhook requests at that URL will be accepted. You can specify the number of the trigger to remove as well: `@abbot remove 2`.
* `@abbot cloud-event remove {triggerUrl} {origin}` - removes the specific allowed origin for the trigger URL. You can specify the order of the trigger instead of the trigger URL. Ex. `@abbot cloud-event remove 2 example.com` removes `example.com` from the allowed origin for the second trigger.
--------------------------------------------------------------------------------------------------- */

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

if (Bot.IsChat) {
    // This section describes the chat interactions
    var (cmd, arg, origin) = Bot.Arguments;
    
    Task reply = (cmd, arg, origin) switch {
        (IMissingArgument, _, _) => ReplyWithHelp(),
        ({Value: "list"}, IMissingArgument, _) => ReplyWithListTriggersWithAllowedOrigins(),
        ({Value: "list"}, var triggerArg, IMissingArgument) => ReplyWithListOfAllowedWebHooks(triggerArg),
        ({Value: "remove"}, var triggerArg, IMissingArgument) => RemoveAllAllowedOriginsAndReply(triggerArg),
        ({Value: "remove"}, var triggerArg, var originArg) => RemoveAllowedOriginAndReply(triggerArg, originArg.Value),
        _ => ReplyWithLackOfUnderstanding()
    };
    
    await reply;
    return;
}

/* -----------------------------------------------
The following methods handle webhook interactions.
-------------------------------------------------- */
if (Bot.Request.HttpMethod == HttpMethod.Options) {
    await HandleValidationOptionsRequest();
    return;
}

if (Bot.Request.IsJson) {
    string origin = Bot.Request.Headers.Origin;
    if (origin is not {Length: > 0}) {
        // Azure Cloud Events are not sending the Origin header, even though the spec requires it. They send this instead.
        // I've reported this to the proper authorities.
        origin = Bot.Request.Headers.WebHookRequestOrigin;
    }

    var allowedOrigins = await GetAllowedOrigins();
    if (!allowedOrigins.Contains(origin)) {
        await Bot.ReplyAsync($"Rejected origin `{origin}`.");
        return;
    }
    
    var cloudEvent = GetCloudEvent();
    
    Task reply = cloudEvent.Type switch {
        "Microsoft.Web.AppUpdated" => ReplyWithAppUpdatedMessage(cloudEvent),
        "Microsoft.EventGrid.SubscriptionDeletedEvent" => Bot.ReplyAsync($"Azure EventGrid Subscription `{cloudEvent.Data.ToObject<SubscriptionData>().EventSubscriptionId}` deleted. This message came from {Bot.SkillUrl}"),
        _ when cloudEvent.Type.StartsWith("Microsoft.Web.Slot") => ReplyWithSlotSwapMessage(cloudEvent),
        _ => Bot.ReplyAsync($"Received an unknown event. Version: `{Bot.VersionInfo.ProductVersion}`. You may want to check the Activity logs for the trigger to see what's up. This message came from {Bot.SkillUrl}.")
    };
    
    await reply;
    return;
}

await Bot.ReplyAsync("Received an unknown Cloud Event request. You may want to check the Activity logs to see what's up. This message came from {Bot.SkillUrl}..");

Task ReplyWithSlotSwapMessage(CloudEvent cloudEvent) {
    var slotSwapData = cloudEvent.Data.ToObject<SlotSwapData>();
    if (cloudEvent.Type.StartsWith("Microsoft.Web.SlotSwap")) {
        string action = cloudEvent.Type switch {
                "Microsoft.Web.SlotSwapCompleted" => "completed",
                "Microsoft.Web.SlotSwapStarted" => "started",
                "Microsoft.Web.SlotSwapFailed" => "failed",
                "Microsoft.Web.SlotSwapWithPreviewStarted" => "started with preview",
                "Microsoft.Web.SlotSwapWithPreviewCancelled" => "cancelled with preview",
                _ => cloudEvent.Type
            };
        
        // https://docs.microsoft.com/en-us/azure/event-grid/event-schema-app-service?tabs=cloud-event-schema
        // says we should get "siteName" but the actual payload has "name". I've also reported this to the 
        // proper authorities.
        var siteName = slotSwapData.SiteName ?? slotSwapData.Name;
        return Bot.ReplyAsync($"{siteName}: Swap from {slotSwapData.SourceSlot} to {slotSwapData.TargetSlot} {action}. Version: `{Bot.VersionInfo.ProductVersion}`.  This message came from {Bot.SkillUrl}.");
    }
    return Bot.ReplyAsync("Something happened with an app service but I don't know what.");
}

Task ReplyWithAppUpdatedMessage(CloudEvent cloudEvent) {
    var appUpdatedData = cloudEvent.Data.ToObject<AppUpdatedData>();
    var siteName = appUpdatedData.SiteName ?? appUpdatedData.Name;
    return Bot.ReplyAsync($"{siteName}: Update {appUpdatedData.AppEventTypeDetail.Action}. Version: `{Bot.VersionInfo.ProductVersion}`.  This message came from {Bot.SkillUrl}.");
}

public CloudEvent GetCloudEvent() {
    // It's possible to subscribe to event batches, hence this haaaaack.
    if (Bot.Request.RawBody.StartsWith('[')) {
        var list = Bot.Request.DeserializeBodyAs<List<CloudEvent>>();
        if (list is null or not { Count: 1 }) {
            return default;
        }
        return list.Single();
    }
    return Bot.Request.DeserializeBodyAs<CloudEvent>();
}

public class CloudEvent {
    public string Id { get; set; }
    public string Source { get; set; }
    public string SpecVersion { get; set; }
    public string Type { get; set; }
    public string DataSchema { get; set; }
    public string Subject { get; set; }
    public DateTimeOffset Time { get; set; }
    public JObject Data { get; set; }
}

public class AppServiceData {
    public string Name { get; set; }
    // https://docs.microsoft.com/en-us/azure/event-grid/event-schema-app-service?tabs=cloud-event-schema says we should get "siteName" but in 
    // my testing I'm getting "name"
    public string SiteName { get; set; }
    public string ClientRequestID { get; set; }
    public string CorrelationRequestId { get; set; }
    public string RequestId { get; set; }
    public string Address { get; set; }
    public string Verb { get; set; }
}

public class AppUpdatedData : AppServiceData {
    public AppEventTypeDetail AppEventTypeDetail { get; set; }
}

public class AppEventTypeDetail {
    public string Action { get; set; }
}

public class SlotSwapData : AppServiceData
{
    public string SourceSlot { get; set; }
    public string TargetSlot { get; set; }
}

public class SubscriptionData {
    public string EventSubscriptionId { get; set; }
}

// Handles the validation request: https://github.com/cloudevents/spec/blob/v1.0.1/http-webhook.md#41-validation-request
async Task HandleValidationOptionsRequest() {
    // This is the origin that is requesting permission to send requests to this trigger.
    string origin = Bot.Request.Headers.WebHookRequestOrigin;
    
    // We want to save that this origin may send events to this specific trigger. Multiple origins may be 
    // allowed to post to the same trigger so we use the trigger URL as the key.
    await AddToAllowedOrigins(origin);
    
    // Respond that this origin is allowed. This part of the spec.
    Bot.Response.Headers.WebHookAllowedOrigin = origin;
    Bot.Response.Headers.WebHookAllowedRate = 120;
    
    // And we should report to the room that it all went to plan.
    await Bot.ReplyAsync($"Validated that {origin} may deliver cloud events to this channel.");
}

/* ----------------------------------------------
The following methods handle chat interactions.
------------------------------------------------- */

async Task ReplyWithListTriggersWithAllowedOrigins() {
    var allTriggers = await GetAllTriggers();
    var triggerList = allTriggers.Select(subscription => subscription.Key.ToLowerInvariant())
        .ToOrderedList();
    if (!triggerList.Any()) {
        await Bot.ReplyAsync("There are no allowed origins for this skill. `{Bot} help {Bot.SkillName}` for help using this skill.");
        return;
    } 
    await Bot.ReplyAsync($"These are the triggers for this skill that allow WebHooks. `{Bot} {Bot.SkillName} list #` or `{Bot} {Bot.SkillName} list {{url}}` to see which origins are allowed for that trigger:\n{triggerList}");
}

async Task ReplyWithListOfAllowedWebHooks(IArgument triggerArg) {
    var triggerUrl = await GetTriggerUrlFromArgument(triggerArg);
    var allowedOrigins = await GetAllowedOrigins(triggerUrl);
    if (allowedOrigins is null or {Count: 0}) {
        await Bot.ReplyAsync($"There are no origins allowed to post to {triggerUrl}. That sounds like a bug in this skill.");
        return;
    }
    await Bot.ReplyAsync($"These are the webhooks allowed to post to {triggerUrl}:\n{allowedOrigins.ToMarkdownList()}");
}

Task ReplyWithHelp() {
    return Bot.ReplyAsync(GetHelpMessage());
}

Task ReplyWithLackOfUnderstanding() {
    return Bot.ReplyAsync($"Sorry, I did not understand that. {GetHelpMessage()}");
}

async Task RemoveAllAllowedOriginsAndReply(IArgument triggerArg) {
    var triggerUrl = await GetTriggerUrlFromArgument(triggerArg);
    await Bot.Brain.DeleteAsync(triggerUrl);
    await Bot.ReplyAsync($"Removed all webhooks allowed to post to {triggerUrl}. You may still want to go to the source and delete the webhook registration.");
    return;    
}

async Task RemoveAllowedOriginAndReply(IArgument triggerArg, string origin) {
    var triggerUrl = await GetTriggerUrlFromArgument(triggerArg);
    var removed = await Bot.Brain.RemoveFromHashSetAsync(triggerUrl, origin);
    if (removed) {
        await Bot.ReplyAsync($"I removed {origin} from the list of webooks allowed to post to {triggerUrl}. You may still want to go to the source and delete the webhook registration.");
        return;
    }
    await Bot.ReplyAsync($"The origin {origin} was not in the list of allowed origins that may post to {triggerUrl}.");
}

string GetHelpMessage() {
    return $"`{Bot} help {Bot.SkillName}` to learn how to use this skill.";
}

async Task<IReadOnlyList<ISkillDataItem>> GetAllTriggers() {
    var allTriggers = await Bot.Brain.GetAllAsync();
    return allTriggers
        .Where(item => !item.Key.Equals("Log", StringComparison.OrdinalIgnoreCase))
        .ToList();
}

async Task<string> GetTriggerUrlFromArgument(IArgument triggerArg) {
    var position = triggerArg.ToInt32(); // Attempt to convert to int, otherwise return null.
    if (position.HasValue) {
        var allSubscriptions = await GetAllTriggers();
        return allSubscriptions[position.Value - 1].Key.ToLowerInvariant();
    }
    return triggerArg.Value.ToLowerInvariant();
}

Task<HashSet<string>> GetAllowedOrigins() {
    return GetAllowedOrigins(Bot.Request.Url.ToString());
}

Task<HashSet<string>> GetAllowedOrigins(string url) {
    return Bot.Brain.GetHashSetAsync<string>(url);
}

Task AddToAllowedOrigins(string origin) {
    var triggerUrl = Bot.Request.Url.ToString();
    return Bot.Brain.AddToHashSetAsync(triggerUrl, origin);
}
