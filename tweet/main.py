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
#
#  AUTHORIZATION
#  --------------
# `@abbot tweet auth` - to authenticate the Twitter user this skill will manage in the current room.
# `@abbot tweet auth {pin}` - to complete the authentication process. The pin is supplied by Twitter after you authenticate to Twitter in the browser.
# `@abbot tweet auth user` - to find out which Twitter account the current room is authorized to manage.

import re
import requests
from requests_oauthlib import OAuth1
from urllib.parse import quote_plus

ABBOT_TWITTER_CLIENT_URL = 'https://abbot.run/twitter-client-proxy/trigger/yvX1lQH_rgvNjdtwFbmt1Tg7'
secret_key = str(bot.room.cache_key) + "|SKILL_SECRET"

def get_skill_secret():
  return bot.brain.read(secret_key)


SKILL_SECRET = get_skill_secret()


def fetch_auth_url():
    """Make a request to the Abbot Twitter Client for an Auth URL"""
    
    data = {
      'endpoint': 'auth'
    }

    r = requests.post(url=ABBOT_TWITTER_CLIENT_URL, json=data)
    r.raise_for_status()
    return r.json()

  
def set_auth_pin(pin):
    """
    Make a request to the Abbot Twitter Client to supply the authorization PIN for pin based authorization
    https://developer.twitter.com/en/docs/authentication/oauth-1-0a/pin-based-oauth
    """
    data = {
      'endpoint': 'pin',
      'pin': pin,
      'skill_secret': SKILL_SECRET
    }

    r = requests.post(url=ABBOT_TWITTER_CLIENT_URL, json=data)
    r.raise_for_status()
    return r.json()


def get_user():
  """
  Retrieves the current authenticated user.
  """
  data = {
    'endpoint': "account/verify_credentials.json",
    'method': "GET",
    'skill_secret': SKILL_SECRET
  }
  r = requests.post(url=ABBOT_TWITTER_CLIENT_URL, json=data)
  r.raise_for_status()
  return r.json()


def reply_with_current_user():
  try:
    user = get_user()
    if user is None:
      bot.reply("No Twitter user is authorized for this room or the authorization has been revoked. `@abbot tweet auth` to authorize a Twitter account")
      return
    username = user.get("screen_name")
    bot.reply_with_image(user.get("profile_image_url_https"), "The twitter user [@" + username + "](https://twitter.com/" + username + ") is attached to this room.")
  except:
    bot.reply("No Twitter user is authorized for this room or the authorization has been revoked. `@abbot tweet auth` to authorize a Twitter account")

def write_skill_secret(secret):
  bot.brain.write(secret_key, secret)

  
def delete_skill_secret():
  bot.brain.delete(secret_key)

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
    """Make a request to the Abbot Twitter Client"""
    
    if kwargs:
        querystring = "&".join(["{}={}".format(k, quote_plus(v)) for (k, v) in kwargs.items()])
        endpoint = "{}?{}".format(action, querystring)
    else:
        endpoint = action
    
    data = {
      'endpoint': endpoint,
      'method': 'POST',
      'skill_secret': SKILL_SECRET
    }

    r = requests.post(url=ABBOT_TWITTER_CLIENT_URL, json=data)
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
        # Replace @@haacked with @haacked. This gives us an escape hatch when a twitter username matches Slack.
        tweet_text = re.sub(r'(?:(?<=^)|(?<=\s))@@([a-zA-Z0-9_]{1,15})', r'@\1', bot.arguments)
        # if tweet_text contains a subscring of the form <@U.?> then reply that it is not valid
        if re.search(r'<@U.+?>', tweet_text):
            bot.reply("Whoops, it looks like you intended to include a Twitter username that just happens to match a Slack username. You can use `@@` in that case. For example, for Twitter user `@haacked` you can specify `@@haacked`")
        else:
            results = send("statuses/update.json", status=tweet_text)
            user = results.get("user")
            screen_name = user.get("screen_name")
            tweet_id = results.get("id")
            bot.reply(":boom: just tweeted it! :point_right: https://twitter.com/{}/status/{}".format(screen_name, tweet_id))


# Main dispatch logic
def main():
    words = bot.arguments.split(" ")
    cmd = words[0].upper()
    param = None
    
    if cmd == "AUTH":
      if len(words) == 1:
        # Initiate Auth.
        response = fetch_auth_url()
        skill_secret = response.get("skill_secret")
        auth_url = response.get("auth_url")
        write_skill_secret(skill_secret)
        bot.reply("Please [click here](" + auth_url + ") to authenticate this skill with Twitter. After you authenticate, tell me the pin like so: `@abbot " + bot.skill_name + " auth {pin}` ")
      else:
        pin = words[1]
        if (pin == "clear"):
          delete_skill_secret()
          bot.reply("Cleared authentication info for this room")
        elif (pin == "user"):
          reply_with_current_user()
        else: # Confirm the PIN
          response = set_auth_pin(pin)
          success = response.get("success")
          if (success):
            reply_with_current_user()
            return
          bot.reply(response.get("message"))
    elif cmd == "USER":
      reply_with_current_user()
    else: # Run the rest of the commands, but make sure SKILL_SECRET is set.
      if SKILL_SECRET is None:
        bot.reply("To set up this skill, run `@abot " + bot.skill_name + " auth` to authenticate with the Twitter account you want to manage with this skill from this room")
        return
      if len(words) > 1:
        param = words[1]
        tweet(cmd, param, words)
      else:
        bot.reply("Try `@abbot help tweet` to learn how to use the `tweet` skill.")


main()
