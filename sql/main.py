import pandas
import sqlalchemy


def run_query(query):
  # Set a secret called "connstring" that contains the connection to your database.
  connstr = bot.secrets.read("connstring")

  if connstr:
    engine = sqlalchemy.create_engine(connstr)
    df = pandas.read_sql(query, engine)
    return "```{}```".format(df.to_markdown())
  else:
    return "There's no connection string set up. Please add one before running this skill."

  
# We recommend predefining some queries instead of letting people run SQL 
# commands directly from chat.
queries = {
    "newusers": """ SELECT "Username", "CreatedAt" 
                    FROM "Users" 
                    ORDER BY "CreatedAt" 
                    DESC LIMIT 10;""",
    "usercount": """ SELECT DATE(date_joined) AS DAY, COUNT(id) AS NewUsers 
                     FROM users 
                     GROUP BY DATE(date_joined);""",
    "reactions": """ SELECT "reaction_type", count("reaction_id") as reactioncount 
                    FROM "reactions"  
                    GROUP BY "reaction_type" 
                    ORDER BY reactioncount desc 
                    LIMIT 7; """
}

query_keys = queries.keys()

# Check to see if the user has asked for a predefined query.
if bot.arguments in query_keys:
  result = run_query(queries.get(bot.arguments))
else:
  result = "Available queries are: "
  for key in query_keys:
    result += "\n * " + key


# Return the result to chat
bot.reply(result)