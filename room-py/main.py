def handle_topic(args):
    if (len(args) == 0):
      bot.reply('Please specify a topic')
      return
    possible_room_arg = args[0]
    room = get_room(possible_room_arg)
    topic = args[1:].value if (isinstance(possible_room_arg, RoomArgument)) else args.value
    
    result = bot.rooms.set_topic(room, topic)
    if (result.ok):
      bot.reply('Room topic set successfully')
    else:
      bot.reply(f'Error setting room topic {result.error}')


def handle_purpose(args):
    if (len(args) == 0):
      bot.reply('Please specify a purpose')
      return
    possible_room_arg = args[0]
    room = get_room(possible_room_arg)
    purpose = args[1:].value if (isinstance(possible_room_arg, RoomArgument)) else args.value
    result = bot.rooms.set_purpose(bot.room, purpose)
    if (result.ok):
      bot.reply('Room purpose set successfully')
    else:
      bot.reply(f'Error setting room purpose {result.error}')


def create_room(args):
    if (len(args) != 1):
        bot.reply('Usage: `{bot} create {room-name}`')
        return;
    room_name = args.value
    result = bot.rooms.create(room_name)
    if (result.ok):
        bot.reply(f'Created room {result.value}. Invite users to the room with: `{bot} {bot.skill_name} invite @mention1 @mention2 ... to {result.value}`')
    else:
        bot.reply(f'Error creating room {result.error}')


def archive_room(args):
    if (len(args) != 1):
        bot.reply('Usage: `{bot} archive {#room-mention}`')
        return;
    room_arg = args[0]
    if not isinstance(room_arg, RoomArgument):
        bot.reply('Usage: `{bot} archive {#room-mention}`')
        return
    room = room_arg.room
    result = bot.rooms.archive(room)
    if (result.ok):
        bot.reply(f'Archived room {room.name}.')
    else:
        bot.reply(f'Error archiving room {result.error}')
        
        
def invite_users_to_room(args):
    if (len(args) < 2):
      bot.reply('Usage: `{bot} invite {#room} {@mention1} {@mention2} ... {@mentionN}`')
      return
    room_arg = args[0]
    if (not isinstance(room_arg, RoomArgument)):
      bot.reply('Second argument must be a room reference. Usage: `{bot} invite {#room} {@mention1} {@mention2} ... {@mentionN}`')
      return
    room = room_arg.room
    mention_args = filter(lambda arg: isinstance(arg, MentionArgument), args)
    users = [mention_arg.mentioned for mention_arg in mention_args]
    if (len(users) == 0):
      bot.reply("Need to mention at least one user to invite to the room.")
      return
    
    bot.rooms.invite_users(room, users)

    
def get_room(arg):
    return arg.room if (isinstance(arg, RoomArgument)) else bot.room

    
if (len(bot.tokenized_arguments) == 0):
    bot.reply(f'`{bot} help {bot.skill_name}` for help on this skill.')
else:
    cmd = bot.tokenized_arguments[0].value
    # get the elements bot.tokenized_arguments without the first element
    args = bot.tokenized_arguments[1:]
    
    if cmd == 'topic':
        handle_topic(args)
    elif cmd == 'purpose':
        handle_purpose(args)
    elif cmd == 'create':
        create_room(args)
    elif cmd == 'invite':
        invite_users_to_room(args)
    elif cmd == 'archive':
        archive_room(args)
    else:
        bot.reply(f'Unknown command. `{bot} help {bot.skill_name}` for help on this skill.')