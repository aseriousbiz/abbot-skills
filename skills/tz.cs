/*
Description: Convert time to the timezones for mentioned users.

Usage:

`@abbot tz` _replies with the current time in Abbot's timezone_
`@abbot tz me` _replies with the current time in your timezone._
`@abbot tz {time} @user1 @user2` _replies with the {time} in each of the user's timezones. It uses the first mention's timezone as the basis for the time._
For example: `@abbot tz 2pm me @somebody @another` will show 2pm in my timezone converted to the timezones of @somebody and @another.
`@abbot tz 2pm @somebody me` will show 2pm in @somebody's timezone converted to my timezone.
*/

if (Bot.Arguments is { Count: 0 }) {
    await Bot.ReplyAsync($"Hello, it is `{GetLocalTime(Bot.TimeZone)}` in my timezone. Try `{Bot} help tz` to learn more about what I can do with timezones.");
    return;
}

var time = Bot.Arguments.First();

// If no target time is specified, use the current time for the user or Abbot
// If we do not know the user's timezone.
var targetTime = time.ToLocalTime() is LocalTime localTime
    ? localTime
    : Bot.From.GetLocalTime();

if (targetTime is null) {
    await Bot.ReplyAsync($"I do not know your timezone. You can tell me using `{Bot} my tz is {{tz}}` (where tz is the TZ database name in https://en.wikipedia.org/wiki/List_of_tz_database_time_zones) or by telling me your location, `@abbot my location is {{zip, city, or address}}`");
    return;
}

var mentions = GetOrderedNormalizedMentions();

if (mentions is { Count: 0 }) {
    await Bot.ReplyAsync($"Mention some users to see this time in their timezones.");
    return;
}

var timeTable = GetTimeData(mentions, targetTime.Value);
await Bot.ReplyTableAsync(timeTable);
return;

IEnumerable<UserTimeZone> GetTimeData(IList<IChatUser> mentions, LocalTime localTime) {
    // Use the timezone for the first mention.
    var sourceTz = mentions.First().TimeZone;

    foreach (var mention in mentions) {
        var mentionTz = mention.TimeZone;
        if (mentionTz is null) {
            yield return new UserTimeZone(mention.Name);
        }
        else {
            var time = localTime.ToTimeZone(sourceTz, mentionTz).TimeOfDay;
            yield return new UserTimeZone(mention.Name, mentionTz.Id, time.ToString());
        }
    }
}

public class UserTimeZone {
    public UserTimeZone(string user) : this (user, "(unknown)", "(unknown)") {
    }
    
    public UserTimeZone(string user, string timezone, string time) {
        User = user;
        TimeZone = timezone;
        Time = time;
    }
    
    public string User { get; }
    public string Time { get; }
    public string TimeZone { get; }
}

static string GetLocalTime(DateTimeZone tz) {
    if (tz is null) {
        return null;
    }
    var now = SystemClock.Instance.GetCurrentInstant();
    return $"{now.InZone(tz):MMM dd, uuuu h:mm:ss tt o<G>} {tz}";
}

IList<IChatUser> GetOrderedNormalizedMentions() {
    return Bot.Arguments.Select(arg => arg is IMentionArgument mention
                                ? mention.Mentioned
                                : arg.Value is "me"
                                   ? Bot.From
                                   : null)
        .Where(user => user is not null)
        .ToList();
}

static Instant GetCurrentInstant(){
    return SystemClock.Instance.GetCurrentInstant();
}
