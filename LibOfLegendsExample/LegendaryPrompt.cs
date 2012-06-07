﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using FluorineFx.Net;

using Nil;

using LibOfLegends;

using com.riotgames.platform.statistics;
using com.riotgames.platform.summoner;
using com.riotgames.platform.gameclient.domain;

namespace LibOfLegendsExample
{
	class LegendaryPrompt
	{
		bool Running;

		RPCService RPC;
		AutoResetEvent OnConnectEvent;
		bool ConnectionSuccess;

		Dictionary<string, CommandInformation> CommandDictionary;

		Configuration ProgramConfiguration;

		ConnectionProfile ConnectionData;

		public LegendaryPrompt(Configuration configuration, ConnectionProfile connectionData)
		{
			ProgramConfiguration = configuration;
			ConnectionData = connectionData;
			InitialiseCommandDictionary();
			Running = true;
		}

		public void Run()
		{
			while (Running)
			{
				OnConnectEvent = new AutoResetEvent(false);
				ConnectionSuccess = false;
				WriteWithTimestamp("Connecting to server...");
				try
				{
					RPC = new RPCService(ConnectionData, OnConnect, OnDisconnect, OnNetStatus);
					RPC.Connect();
				}
				catch (Exception exception)
				{
					Output.WriteLine("Connection error: " + exception.Message);
				}
				OnConnectEvent.WaitOne();
				if (ConnectionSuccess)
					PerformQueries();
			}
		}

		void WriteWithTimestamp(string input, params object[] arguments)
		{
			string message = string.Format("[{0}] {1}", Time.Timestamp(), input);
			Output.WriteLine(message, arguments);
		}

		void OnConnect(RPCConnectResult result)
		{
			ConnectionSuccess = result.Success();
			Output.WriteLine(result.GetMessage());
			OnConnectEvent.Set();
		}

		void OnDisconnect()
		{
			if(Running)
				WriteWithTimestamp("Disconnected");
		}

		void OnNetStatus(NetStatusEventArgs eventArguments)
		{
			/*
			WriteWithTimestamp("OnNetStatus:");
			foreach (var pair in eventArguments.Info)
					Output.WriteLine("{0}: {1}", pair.Key, pair.Value);
			*/
		}

		void ProcessLine(string line)
		{
			List<string> arguments = line.Split(new char[] {' '}).ToList();
			if (line.Length == 0 || arguments.Count == 0)
				return;

			string command = arguments[0];
			arguments.RemoveAt(0);

			CommandInformation commandInformation;
			if (!CommandDictionary.TryGetValue(command, out commandInformation))
			{
				Output.WriteLine("Unrecognised command, enter \"help\" for a list of commands");
				return;
			}


			if (commandInformation.ArgumentCount != -1 && arguments.Count != commandInformation.ArgumentCount)
			{
				Output.WriteLine("Invalid number of arguments specified");
				return;
			}

			commandInformation.Handler(arguments);
		}

		void PerformQueries()
		{
			while (Running)
			{
				Console.Write("> ");
				string line = Console.ReadLine();
				try
				{
					ProcessLine(line);
				}
				catch (RPCTimeoutException)
				{
					Output.WriteLine("RPC timeout occurred");
					RPC.Disconnect();
					return;
				}
				catch (Exception exception)
				{
					Output.WriteLine("An exception occurred:");
					Output.WriteLine(exception.Message);
					break;
				}
			}
		}

		void InitialiseCommandDictionary()
		{
			CommandDictionary = new Dictionary<string, CommandInformation>()
			{
				{"quit", new CommandInformation(0, Quit, "", "Terminates the application")},
				{"help", new CommandInformation(0, PrintHelp, "", "Prints this help")},
				{"profile", new CommandInformation(-1, AnalyseSummonerProfile, "<name>", "Retrieve general information about the summoner with the specified name")},
				{"ranked", new CommandInformation(-1, RankedStatistics, "<name>", "Analyse the ranked statistics of the summoner given")},
				{"recent", new CommandInformation(-1, AnalyseRecentGames, "<name>", "Analyse the recent games of the summoner given")},
				{"runes", new CommandInformation(-1, RunePages, "<name>", "View rune pages")},
				{"test", new CommandInformation(1, RunTest, "<ID>", "Run summoner ID vs. account ID test")},
			};
		}

		void Quit(List<string> arguments)
		{
			RPC.Disconnect();
			Running = false;
		}

		void PrintHelp(List<string> arguments)
		{
			Output.WriteLine("List of commands available:");
			foreach (var entry in CommandDictionary)
			{
				var information = entry.Value;
				Output.WriteLine(entry.Key + " " + information.ArgumentDescription);
				Output.WriteLine("\t" + information.Description);
			}
		}

		string GetNameFromArguments(List<string> arguments)
		{
			string summonerName = "";
			bool first = true;
			foreach (var argument in arguments)
			{
				if (first)
					first = false;
				else
					summonerName += " ";
				summonerName += argument;
			}
			return summonerName;
		}

		void NoSuchSummoner()
		{
			Output.WriteLine("No such summoner");
		}

		static int CompareGames(PlayerGameStats x, PlayerGameStats y)
		{
			return - x.createDate.CompareTo(y.createDate);
		}

		bool GetRecentGames(string summonerName, ref PublicSummoner publicSummoner, ref List<PlayerGameStats> recentGames)
		{
			publicSummoner = RPC.GetSummonerByName(summonerName);
			if (publicSummoner == null)
				return false;
			RecentGames recentGameData = RPC.GetRecentGames(publicSummoner.acctId);
			recentGames = recentGameData.gameStatistics;
			recentGames.Sort(CompareGames);
			return true;
		}

		void AnalayseStatistics(string description, string target, List<PlayerStatSummary> summaries)
		{
			foreach (var summary in summaries)
			{
				if (summary.playerStatSummaryType == target)
				{
					int games = summary.wins + summary.losses;
					if (games == 0)
						continue;
					Output.Write("{0}: ", description);
					//Check for the unranked/Dominion bogus value signature
					if (summary.rating != 400 && summary.maxRating != 0)
						Output.Write("{0} (top {1}), ", summary.rating, summary.maxRating);
					Output.Write("{0} W - {1} L ({2})", summary.wins, summary.losses, SignPrefix(summary.wins - summary.losses));
					if (summary.leaves > 0)
						Output.Write(", left {0} {1}", summary.leaves, (summary.leaves > 1 ? "games" : "game"));
					Output.WriteLine("");
					return;
				}
			}
		}

		void AnalyseSummonerProfile(List<string> arguments)
		{
			string summonerName = GetNameFromArguments(arguments);
			PublicSummoner publicSummoner = new PublicSummoner();
			List<PlayerGameStats> recentGames = new List<PlayerGameStats>();
			bool foundSummoner = GetRecentGames(summonerName, ref publicSummoner, ref recentGames);
			if (!foundSummoner)
			{
				NoSuchSummoner();
				return;
			}

			PlayerLifeTimeStats lifeTimeStatistics = RPC.RetrievePlayerStatsByAccountID(publicSummoner.acctId, "CURRENT");
			if (lifeTimeStatistics == null)
			{
				Output.WriteLine("Unable to retrieve lifetime statistics");
				return;
			}

			List<PlayerStatSummary> summaries = lifeTimeStatistics.playerStatSummaries.playerStatSummarySet;

			Output.WriteLine("Name: " + publicSummoner.name);
			Output.WriteLine("Account ID: " + publicSummoner.acctId);
			Output.WriteLine("Summoner ID: " + publicSummoner.summonerId);
			Output.WriteLine("Summoner level: " + publicSummoner.summonerLevel);

			//The hidden "Team" variants of the "Premade" ratings are currently unused, it seems
			AnalayseStatistics("Unranked Summoner's Rift/Twisted Treeline", "Unranked", summaries);
			AnalayseStatistics("Ranked Twisted Treeline (team)", "RankedPremade3x3", summaries);
			AnalayseStatistics("Ranked Summoner's Rift (solo)", "RankedSolo5x5", summaries);
			AnalayseStatistics("Ranked Summoner's Rift (team)", "RankedPremade5x5", summaries);
			AnalayseStatistics("Unranked Dominion", "OdinUnranked", summaries);
		}

		string GetPrefix(int input)
		{
			if (input >= 0)
				return "+";
			return "-";
		}

		string SignPrefix(int input)
		{
			return GetPrefix(input) + Math.Abs(input).ToString();
		}

		void AnalyseRecentGames(List<string> arguments)
		{
			string summonerName = GetNameFromArguments(arguments);
			PublicSummoner publicSummoner = new PublicSummoner();
			List<PlayerGameStats> recentGames = new List<PlayerGameStats>();
			bool foundSummoner = GetRecentGames(summonerName, ref publicSummoner, ref recentGames);
			if (!foundSummoner)
			{
				NoSuchSummoner();
				return;
			}
			foreach (var stats in recentGames)
			{
				GameResult result = new GameResult(stats);
				Console.Write("[{0}] [{1}] [{2}] ", stats.gameId, stats.createDate, result.Win ? "W" : "L");
				if (stats.ranked)
					Console.Write("Ranked ");
				if (stats.gameType == "PRACTICE_GAME")
				{
					Console.Write("Custom ");
					switch (stats.gameMode)
					{
						case "CLASSIC":
							Console.Write("Summoner's Rift");
							break;

						case "ODIN":
							Console.Write("Dominion");
							break;

						default:
							Console.Write(stats.gameMode);
							break;
					}
				}
				else
				{
					switch (stats.queueType)
					{
						case "RANKED_TEAM_3x3":
							Output.Write("Twisted Treeline");
							break;

						case "NORMAL":
						case "RANKED_SOLO_5x5":
							Console.Write("Summoner's Rift");
							break;

						case "RANKED_TEAM_5x5":
							Console.Write("Summoner's Rift (team)");
							break;

						case "ODIN_UNRANKED":
							Console.Write("Dominion");
							break;

						case "BOT":
							Console.Write("Co-op vs. AI");
							break;

						default:
							Console.Write(stats.queueType);
							break;
					}
				}
				Console.WriteLine(", {0}, {1}/{2}/{3}", GetChampionName(stats.championId), result.Kills, result.Deaths, result.Assists);
				List<string> units = new List<string>();
				if (stats.adjustedRating != 0)
					units.Add(string.Format("Rating: {0} ({1})", stats.rating + stats.eloChange, SignPrefix(stats.eloChange)));
				if (stats.adjustedRating != 0)
					units.Add(string.Format("adjusted {0}", stats.adjustedRating));
				if (stats.teamRating != 0)
					units.Add(string.Format("team {0} ({1})", stats.teamRating, SignPrefix(stats.teamRating - stats.rating)));
				PrintUnits(units);
				if (stats.KCoefficient != 0)
					units.Add(string.Format("K coefficient {0}", stats.KCoefficient));
				if (stats.predictedWinPct != 0.0)
					units.Add(string.Format("Predicted winning percentage {0}", Percentage(stats.predictedWinPct)));
				if (stats.premadeSize > 1)
					units.Add(string.Format("Queued with {0}", stats.premadeSize));
				if (stats.leaver)
					units.Add("Left the game");
				if (stats.afk)
					units.Add("AFK");
				units.Add(string.Format("{0} ms ping", stats.userServerPing));
				units.Add(string.Format("{0} s spent in queue", stats.timeInQueue));
				PrintUnits(units);
			}
		}

		void PrintUnits(List<string> units)
		{
			if (units.Count > 0)
				Output.WriteLine(string.Join(", ", units));
			units.Clear();
		}

		string Percentage(double input)
		{
			return string.Format("{0:0.0%}", input);
		}

		string Round(double input)
		{
			return string.Format("{0:0.0}", input);
		}

		int CompareNames(ChampionStatistics x, ChampionStatistics y)
		{
			return x.Name.CompareTo(y.Name);
		}

		void RankedStatistics(List<string> arguments)
		{
			string summonerName = GetNameFromArguments(arguments);
			PublicSummoner publicSummoner = RPC.GetSummonerByName(summonerName);
			if (publicSummoner == null)
			{
				NoSuchSummoner();
				return;
			}
			AggregatedStats aggregatedStatistics = RPC.GetAggregatedStats(publicSummoner.acctId, "CLASSIC", "CURRENT");
			if (aggregatedStatistics == null)
			{
				Output.WriteLine("Unable to retrieve aggregated statistics");
				return;
			}
			List<ChampionStatistics> statistics = ChampionStatistics.GetChampionStatistics(aggregatedStatistics);
			foreach (var entry in statistics)
				entry.Name = GetChampionName(entry.ChampionId);
			statistics.Sort(CompareNames);
			foreach (var entry in statistics)
				Output.WriteLine(entry.Name + ": " + entry.Wins + " W - " + entry.Losses + " L (" + SignPrefix(entry.Wins - entry.Losses) + "), " + Percentage(entry.WinRatio()) + ", " + Round(entry.KillsPerGame()) + "/" + Round(entry.DeathsPerGame()) + "/" + Round(entry.AssistsPerGame()) + ", " + Round(entry.KillsAndAssistsPerDeath()));
		}

		string GetChampionName(int championId)
		{
			string name;
			if (!ProgramConfiguration.ChampionNames.TryGetValue(championId, out name))
				name = string.Format("Champion {0}", championId);
			return name;
		}

		void RunePages(List<string> arguments)
		{
			string summonerName = GetNameFromArguments(arguments);
			PublicSummoner summoner = RPC.GetSummonerByName(summonerName);
			if (summoner == null)
			{
				NoSuchSummoner();
				return;
			}

			AllPublicSummonerDataDTO allSummonerData = RPC.GetAllPublicSummonerDataByAccount(summoner.acctId);
			if (allSummonerData == null)
			{
				Console.WriteLine("Unable to retrieve all public summoner data");
				return;
			}

			if (allSummonerData.spellBook == null)
			{
				Console.WriteLine("Spell book not available");
				return;
			}

			if (allSummonerData.spellBook.bookPages == null)
			{
				Console.WriteLine("Spell book pages not available");
				return;
			}

			foreach (var page in allSummonerData.spellBook.bookPages)
			{
				Console.WriteLine("[{0}] {1} ({2})", page.createDate, page.name, page.current ? "active" : "not active");
				foreach (var slot in page.slotEntries)
					Console.WriteLine("Slot {0}: {1}", slot.runeSlotId, slot.runeId);
			}
		}

		void RunTest(List<string> arguments)
		{
			int id = Convert.ToInt32(arguments[0]);

			AllSummonerData summonerData = RPC.GetAllSummonerDataByAccount(id);
			AllPublicSummonerDataDTO allSummonerData = RPC.GetAllPublicSummonerDataByAccount(id);

			Console.WriteLine("getAllSummonerDataByAccount: {0}", summonerData != null);
			Console.WriteLine("getAllPublicSummonerDataByAccount: {0}", allSummonerData != null);
		}
	}
}

