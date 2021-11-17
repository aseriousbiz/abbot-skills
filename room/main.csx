#load ".meta/globals.csx" // This is required for Intellisense in VS Code, etc. DO NOT TOUCH THIS LINE!
Task action = Bot.Arguments switch {
    ({Value: "create"}, var room) => CreateRoomAsync(room.Value),
    ({Value: "archive"}, IRoomArgument roomArg) => ArchiveRoomAsync(roomArg.Room),
    ({Value: "invite"}, _) => InviteUsersAsync(Bot.Arguments.Skip(1)), // Skip the invite part.
    (IRoomArgument roomArg, {Value: "topic"}, {Value: "is"}, var topicArg) => SetRoomTopicAsync(roomArg.Room, topicArg.Value),
    (IRoomArgument roomArg, {Value: "purpose"}, {Value: "is"}, var purposeArg) => SetRoomPurposeAsync(roomArg.Room, purposeArg.Value),
    (IRoomArgument roomArg, {Value: "topic"}, var topicArg) => SetRoomTopicAsync(roomArg.Room, topicArg.Value),
    (IRoomArgument roomArg, {Value: "purpose"}, var purposeArg) => SetRoomPurposeAsync(roomArg.Room, purposeArg.Value),
    ({Value: "topic"}, {Value: "is"}, var topicArg) => SetRoomTopicAsync(Bot.Room, topicArg.Value),
    ({Value: "purpose"}, {Value: "is"}, var purposeArg) => SetRoomPurposeAsync(Bot.Room, purposeArg.Value),
    ({Value: "topic"}, var topicArg) => SetRoomTopicAsync(Bot.Room, topicArg.Value),
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

Task InviteUsersAsync(IArguments args) {
    var users = args.OfType<IMentionArgument>().Select(m => m.Mentioned);
    if (!users.Any()) {
        return Bot.ReplyAsync("Please mention at least one user to invite to the room.");
    }
    
    var rooms = args.OfType<IRoomArgument>().ToList();
    return rooms switch {
            {Count: 0} => Bot.ReplyAsync("Please specify a room to invite the users to."),
            {Count: 2} => Bot.ReplyAsync("Please only specify one room to invite the users to."),
            _ => InviteUsersToRoomAsync(rooms.Single().Room, users)
    };
}

async Task InviteUsersToRoomAsync(IRoom room, IEnumerable<IChatUser> users) {
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
