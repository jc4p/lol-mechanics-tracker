# lol-mechanics-tracker
helping myself be less bad at video games

Okay so. Despite my better judgement, I keep playing League of Legends. It's 5 people vs 5 people, teamwork is important,
but for the first 10-20 min of the game it's just you versus someone else (in most cases). In that phase, there's a lot of
little stuff that you do to gain small wins, which hopefully later on build up to a win in the game. This is an app I'm making
to sit on my second monitor while I play LoL, and track me on some metrics that apply to those small wins.

Currently this repo is in the prototyping stage. What I have is a C# app which uses a lightweight DirectX API to record my
primary monitor's screen<sup>[1]</sup>(http://www.virtualdub.org/blog/pivot/entry.php?id=356), look at the top status bar in LoL, and reecord my "creep score", the number of small minions I've killed.

I'm trying to be pretty open, so current plans can be found in [0.1 Milestone](https://github.com/jc4p/lol-mechanics-tracker/issues/1).

In this image you can see the "32" circled in the gray app on the left (this code) matches the "32" on the top-right. 

![screencap](https://pbs.twimg.com/media/Cll8CAZVEAEXF0U.jpg:large)
