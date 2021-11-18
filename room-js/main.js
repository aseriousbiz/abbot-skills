module.exports.RoomJs = (async () => { // We modularize your code in order to run it.
/**
 * @abbot room-js create {room-name} - Creates a room with the given name.
 * @abbot room-js topic #room {topic} - Sets a topic for the specified room
 * @abbot room-js purpose #room  {purpose} - Sets a purpose for the specified room
 * @abbot room-js topic {topic} - Sets a topic for the current room
 * @abbot room-js purpose {purpose} - Sets a purpose for the current room
 * @abbot room-js archive #room - Archives the specified room
 * @abbot room-js invite #room @mention1 @mention2 ... @mentionN - Invites the specified users to the specified room
 */
  if (bot.tokenizedArguments.length === 0) {
    await bot.reply(`\`${bot} help ${bot.skill_name}\` for help on this skill.`);
    return;
  }

  const cmd = bot.tokenizedArguments[0].value;
  const args = bot.tokenizedArguments.slice(1);
    
  switch (cmd) {
    case "create":
      await createRoom(args);
      break;
    case "topic":
      await setTopic(args);
      break;
    case "purpose":
      await setTopic(args);
      break;
    case "archive":
      await archiveRoom(args);
      break;
    case "invite":
      await inviteUsers(args);
      break;
  }

  async function createRoom(args) {
    if (args.length !== 1) {
      await bot.reply('Usage: `{bot} create {room-name}`');
      return;
    }
    const roomName = args[0].value;
    const result = await bot.rooms.create(roomName);
    if (result.ok) {
        await bot.reply(`Created room ${result.value}. Invite users to the room with: \`${bot} ${bot.skillName} invite ${result.value} {@mention1} {@mention2} ... {@mentionN}\``);
    } else {
        await bot.reply(`Error creating room ${result.error}`);
    }
  }
  
  async function archiveRoom(args) {
    if (args.length !== 1) {
      await bot.reply(`Usage: \`${bot} ${bot.skillName} archive {#room-mention}\``);
      return;
    }
    const roomArg = args[0];
    if (!(roomArg instanceof RoomArgument)) {
      await bot.reply('Mention the room please. Usage: `{bot} archive {#room-mention}`');
      return;
    }
    const room = roomArg.room;
    const result = await bot.rooms.archive(room);
    if (result.ok) {
        await bot.reply(`Archived room ${room}.`);
    } else {
        await bot.reply(`Error archiving room ${result.error}`);
    }
  }
  
  async function inviteUsers(args) {
    if (args.length < 2) {
      await bot.reply(`Usage: \`${bot} ${bot.skillName} invite {#room} {@mention1} {@mention2} ... {@mentionN}\``);
      return;
    }
    const roomArg = args[0];
    if (!(roomArg instanceof RoomArgument)) {
      await bot.reply('Mention the room please. Usage: `{bot} archive {#room-mention}`');
      return;
    }
    const room = roomArg.room;
    const users = args.slice(1)
        .filter(m => m instanceof MentionArgument)
        .map(m => m.mentioned);
    if (users.length === 0) {
      await bot.reply("Need to mention at least one user to invite to the room.");
      return;
    }
    const result = await bot.rooms.inviteUsers(room, users);
    if (result.ok) {
        await bot.reply('Succesfully invited users to room.');
    } else {
        await bot.reply(`Error inviting users to room ${result.error}`);
    }
  }
  
  async function setTopic(args) {
    await setTopicOrPurpose(args, 'topic');
  }
  
  async function setPurpose(args) {
    await setTopicOrPurpose(args, 'purpose');
  }
  
  async function setTopicOrPurpose(args, type) {
    if (args.length === 0) {
      await bot.reply(`Please specify a ${type}`);
      return;
    }
    const possibleRoomArg = args[0]
    let room = null;
    let topicOrPurpose = null;
    if (possibleRoomArg instanceof RoomArgument) {
      room = possibleRoomArg.room;
      topicOrPurpose = args.slice(1).value;
    }
    else {
      room = bot.room;
      topicOrPurpose = args.value;
    }
    const result = type === 'topic' ? await bot.rooms.setTopic(room, topicOrPurpose) : await bot.rooms.setPurpose(room, topicOrPurpose);
    if (result.ok) {
        await bot.reply(`Room ${type} set successfully`);
    } else {
        await bot.reply(`Error setting ${type} ${result.error}`);
    }
  }
})(); // Thanks for building with Abbot!
