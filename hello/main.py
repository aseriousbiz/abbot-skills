steps = [
  """
Welcome to Abbot!
This skill is a brief tutorial that walks through some things to try in the Bot Console.
Abbot responds to commands called \"skills\". For example, `hello` is a skill (the one you just called!).
To learn how to use a skill, type `help` followed by the skill name skill. Say `@abbot help hello` (without the quotes) to learn how to use the `hello` skill. Then say `@abbot hello again` to move to the next step in the tutorial.
  """,
  "Great! Now say `@abbot help` for high level help. Remember, say `@abbot hello again` to move to the next step.",
  "Excellent! say `@abbot skills` to see the list of available skills.",
  "Capital! As we mentioned before, you can use the `help` skill to provide help for individual skills. Let's look at help for the `rem` skill. Say `@abbot help rem`",
  "Wonderful! As you saw, `rem` can store and retrieve information. We seeded it with an item. Say `@abbot rem mind blown`.",
  "Perfect! Some skills are built-in, while others can be installed from the Abbot Package directory. We seeded this console with some Abbot skills and some fake users. The first one we'll try is `tz`. Say `@abbot help tz`",
  "Good. We also created some fake users. Say `@abbot tz 2:30pm me @misspiggy @kermit @bugsbunny` Play around with different times etc.",
  "Neat! We also added the `grafana` skill along with some test data. Say `@abbot grafana db ""home:memory / cpu""`",
  "Nice! say `@abbot joke` a few times to have Abbot tell you some jokes. This is an example of a list skill. Say `@abbot help list` to learn how to create your own lists.",
  "Fantastic! That's all we have to show for now! Feel free to play around with the console here. When you're ready, install Abbot into Slack to experience the full power of Abbot."
]

def get_step():
  stored = bot.brain.get("step" + str(bot.user_id))
  if stored is None:
    return 0
  return int(stored)

def write_step(new_step):
  bot.brain.write("step" + str(bot.user_id), str(new_step))

command = bot.arguments
if command == "reset":
  step = 0
  write_step(0)

step = get_step()

if command == "again" or command == "next":
  step = (step + 1) % len(steps) # modulo to loop around to the beginning when we reach the end.
  write_step(step)
elif command == "previous":
  step = (step - 1) % len(steps) # modulo to loop around to the end when we reach the beginning.
  write_step(step)
elif command == "help":
  bot.reply("Nice try! I think what you want is `@abbot help hello` to get help on the `hello` skill.")

bot.reply(steps[step])
