#load ".meta/globals.csx" // This is required for Intellisense in VS Code, etc. DO NOT TOUCH THIS LINE!
/**
 * @abbot room create {room-name} - Creates a room with the given name.
 * @abbot room topic #room {topic} - Sets a topic for the specified room
 * @abbot room purpose #room  {purpose} - Sets a purpose for the specified room
 * @abbot room topic {topic} - Sets a topic for the current room
 * @abbot room purpose {purpose} - Sets a purpose for the current room
 * @abbot room archive #room - Archives the specified room
 * @abbot room invite #room @mention1 @mention2 ... @mentionN - Invites the specified users to the specified room
 */
Task action = Bot.Arguments switch {
    ({Value: "create"}, var room) => CreateRoomAsync(room.Value),
    ({Value: "archive"}, IRoomArgument roomArg) => ArchiveRoomAsync(roomArg.Room),
    ({Value: "invite"}, IRoomArgument roomArg, _) => InviteUsersAsync(roomArg.Room, Bot.Arguments.Skip(1)), // Skip the invite part.
    ({Value: "topic"}, IRoomArgument roomArg, var topicArg) => SetRoomTopicAsync(roomArg.Room, topicArg.Value),
    ({Value: "topic"}, var topicArg) => SetRoomTopicAsync(Bot.Room, topicArg.Value),
    ({Value: "purpose"}, IRoomArgument roomArg, var purposeArg) => SetRoomPurposeAsync(roomArg.Room, purposeArg.Value),
    ({Value: "purpose"}, var purposeArg) => SetRoomPurposeAsync(Bot.Room, purposeArg.Value),
    _ => Bot.ReplyAsync($"`{Bot} help {Bot.SkillName}` for help on this skill.")
};
await action;

async Task CreateRoomAsync(string room) {
    if (room.StartsWith('#')) {
        room = room[1..];
    }
    var result = await Bot.Rooms.CreateAsync(room);
    var reply = result.Ok
        ? $"Created slack room {result.Value}. Invite users to the room by saying: `{Bot} {Bot.SkillName} invite @mention1 @mention2 ... to {result.Value}`"
        : $"Error creating the room: {result.Error}";
    await Bot.ReplyAsync(reply);
}

async Task InviteUsersAsync(IRoom room, IArguments args) {
    var users = args.OfType<IMentionArgument>().Select(m => m.Mentioned);
    if (!users.Any()) {
        await Bot.ReplyAsync("Please mention at least one user to invite to the room.");
        return;
    }
    
    var result = await Bot.Rooms.InviteUsersAsync(room, Bot.Mentions);
    var reply = result.Ok
        ? "Invitation(s) sent!"
        : $"Error inviting users to the room: {result.Error}";
    await Bot.ReplyAsync(reply);
}

async Task ArchiveRoomAsync(IRoom room) {
    var result = await Bot.Rooms.ArchiveAsync(room);
    var reply = result.Ok
        ? $"Archived slack room {room}."
        : $"Error archiving the room: {result.Error}";
    await Bot.ReplyAsync(reply);
}

async Task SetRoomTopicAsync(IRoom room, string topic) {
    var result = await Bot.Rooms.SetTopicAsync(room, topic);
    var reply = result.Ok
        ? $"Topic set!"
        : $"Error setting the topic: {result.Error}";
    await Bot.ReplyAsync(reply);
}

async Task SetRoomPurposeAsync(IRoom room, string purpose) {
    var result = await Bot.Rooms.SetPurposeAsync(room, purpose);
    var reply = result.Ok
        ? $"Purpose set!"
        : $"Error setting the purpose: {result.Error}";
    await Bot.ReplyAsync(reply);
}
