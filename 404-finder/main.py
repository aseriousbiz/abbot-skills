# Description: Check all the links on a page to make sure they all resolve
#
# Package URL: https://ab.bot/packages/aseriousbiz/404-finder
#
# Usage:
# This skill is best used as a scheduled skill.
#
# @abbot schedule 404-finder

from bs4 import BeautifulSoup
from urllib.parse import urlparse
import requests

# Let's pretend to be Chrome in order to check Twitter 
bot_headers = {'user-agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/70.0.3538.77 Safari/537.36'}

def check_link(url):
  if url.startswith("mailto:") or url.startswith("tel:"):
    return
  r = requests.head(url, headers=bot_headers)
  if r.status_code in [404, 500, 502, 503, 504]:
    bot.reply(":warning: {} returned status **{}**".format(url, r.status_code))

def check_links(url):
  """Check any hyperlinks in the data and report any issues to chat."""
  r = requests.get(url, headers=bot_headers)
  r.raise_for_status()
    
  soup = BeautifulSoup(r.text, 'html.parser')
  links = soup.find_all('a', href=True)
  url_parts = urlparse(url)
  for link in links:
    target = link['href']
    if target[0] == "/" or target[0] == "#":
        target = "{}://{}{}".format(url_parts.scheme, url_parts.netloc, link['href'])
    
    check_link(target)
  bot.reply("Finished checking `{}` for 404s".format(url))

if len(bot.arguments) > 0:
  check_links(bot.arguments)
else:
  bot.reply("You didn't say anything")