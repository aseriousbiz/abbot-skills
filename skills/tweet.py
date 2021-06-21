# Description: A Twitter Skill for Abbot.
#
# Package URL: https://ab.bot/packages/aseriousbiz/tweet
# USAGE:
# `@abbot tweet {the text of your tweet}` -- sends a tweet
# `@abbot tweet length {the text of your tweet}` -- returns the number of characters in your text
# `@abbot tweet RT {a link to a tweet to retweet}` -- retweets a tweet
# `@abbot tweet reply {tweet link} {text of reply}` -- replies to a tweet
# `@abbot tweet follow {username}` -- follows an account
# `@abbot tweet unfollow {username}`  -- unfollows an account
# `@abbot tweet like {link to tweet}` -- likes a tweet (can also use FAV or FAVE)

import re
import requests
from requests_oauthlib import OAuth1
from urllib.parse import quote_plus

CONSUMER_KEY = bot.secrets.read("consumerkey")
CONSUMER_SECRET = bot.secrets.read("consumersecret")
ACCESS_TOKEN = bot.secrets.read("accesstoken")
ACCESS_TOKEN_SECRET = bot.secrets.read("accesstokensecret")

  
def get_tweet_id(link):
    """Try to extract a tweet id from a link"""
    # A representative tweet link: https://twitter.com/haacked/status/842543742523334656
    pattern = re.compile(r'twitter\.com\/(.*)\/status(?:es)?\/([^\/\?]+)', re.IGNORECASE)
    match = pattern.findall(link)
    try:
      if match:
        item = match[0]
        screen_name = item[0]
        tweet_id = item[1]
        return screen_name, tweet_id
      else:
        return None, None
    except:
      return None, None

def prep_reply_text(screen_name, text):
    """Replies have to contain the original tweeter's screen_name"""
    if screen_name in text:
        return text
    else:
        if "@" in screen_name:
            return screen_name + " " + text
        else:
            return "@{} {}".format(screen_name, text)


def send(action, **kwargs):
    """Make a request to the Twitter API"""
    base_url = "https://api.twitter.com/1.1/"
    
    if kwargs:
        querystring = "&".join(["{}={}".format(k, quote_plus(v)) for (k, v) in kwargs.items()])
        url = base_url + "{}?{}".format(action, querystring)
    else:
        url = base_url + action
    
    oauth = OAuth1(CONSUMER_KEY, 
                   client_secret=CONSUMER_SECRET,
                   resource_owner_key=ACCESS_TOKEN,
                   resource_owner_secret=ACCESS_TOKEN_SECRET)
    
    r = requests.post(url=url, auth=oauth)
    r.raise_for_status()
    return r.json()


def tweet(cmd, param, words):
    if cmd == "RT":
        screen_name, tweet_id = get_tweet_id(param)
        if tweet_id:
            results = send("statuses/retweet/{}.json".format(tweet_id))
            bot.reply("Retweeted @{}!".format(screen_name))
        else:
            bot.reply("That doesn't look like a link to a valid Tweet.")
    elif cmd == "REPLY":
        screen_name, tweet_id = get_tweet_id(param)
        if screen_name and tweet_id:
            # The original user's screen_name must be included in the tweet text.
            reply_text = prep_reply_text(screen_name, 
                                         " ".join(words[2::])) # 0 is "RT", 1 is the tweet link
            results = send("statuses/update.json", 
                           status=reply_text,
                           in_reply_to_status_id=tweet_id)
            bot.reply("Nice! I replied to {}'s tweet".format(screen_name))
        else:
            bot.reply("That doesn't look like a valid Tweet.")
    elif cmd == "FOLLOW":
        results = send("friendships/create.json", screen_name=param)
        bot.reply("Aww yeah! Followed {}!".format(results.get("name")))
    elif cmd == "UNFOLLOW":
        results = send("friendships/destroy.json", screen_name=param)
        bot.reply("Okay, I unfollowed {}.".format(results.get("name")))
    elif cmd == "LENGTH":
        charcount = len(bot.arguments)
        if charcount <= 280:
            bot.reply(":white_check_mark: Your tweet is {} characters long.".format(len(bot.arguments)))
        else:
            bot.reply(":warning: Uh oh! This is over 280 characters and might be too long. Try and see, I guess...")
    elif cmd in ["FAV", "FAVE", "LIKE"]:
        screen_name, tweet_id = get_tweet_id(param)
        if tweet_id:
            results = send("favorites/create.json", id=tweet_id)
            bot.reply("Hot dog, I just liked {}'s tweet!".format(screen_name))
        else:
            bot.reply("That doesn't look like a link to a valid Tweet.")
    else:
        results = send("statuses/update.json", status=bot.arguments)
        user = results.get("user")
        screen_name = user.get("screen_name")
        tweet_id = results.get("id")
        bot.reply(":boom: just tweeted it! :point_right: https://twitter.com/{}/status/{}".format(screen_name, tweet_id))

# Main dispatch logic
if None in (CONSUMER_KEY, CONSUMER_SECRET, ACCESS_TOKEN, ACCESS_TOKEN_SECRET):
    bot.reply("A secret required for this skill to run has not been set. Please review and ensure all your secrets have been configured.")
else:
    words = bot.arguments.split(" ")
    cmd = words[0].upper()
    param = None

    if len(words) > 1:
      param = words[1]
      tweet(cmd, param, words)
    else:
      bot.reply("Try `@abbot help tweet` to learn how to use the `tweet` skill.")
