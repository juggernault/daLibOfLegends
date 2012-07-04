﻿using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;

using FluorineFx;
using FluorineFx.Messaging.Messages;
using FluorineFx.Net;

using com.riotgames.platform.clientfacade.domain;
using com.riotgames.platform.gameclient.domain;
using com.riotgames.platform.game;
using com.riotgames.platform.login;
using com.riotgames.platform.statistics;
using com.riotgames.platform.summoner;
using com.riotgames.team.dto;

namespace LibOfLegends
{
	public delegate void ConnectCallbackType(RPCConnectResult result);
	public delegate void DisconnectCallbackType();
	public delegate void NetStatusCallbackType(NetStatusEventArgs eventArguments);

	public class RPCService
	{
		#region Delegates

		ConnectCallbackType ConnectCallback;
		DisconnectCallbackType DisconnectCallback;
		NetStatusCallbackType NetStatusCallback;

		#endregion

		#region Server constants

		public const string EndpointString = "my-rtmps";

		const string SummonerService = "summonerService";
		const string PlayerStatsService = "playerStatsService";
        const string GameStatsService = "gameStatsService";
		const string ClientFacadeService = "clientFacadeService";
		const string SummonerTeamService = "summonerTeamService";

        #endregion

        #region Service Declarations

        public GameService game;
        public MatchmakerService matchmaker;
        public SummonerService summoner;

        #endregion

        #region Configuration variables

        ConnectionProfile ConnectionData;

		#endregion

		#region Runtime variables

		public NetConnection NetConnection { get { return RPCNetConnection; } set { RPCNetConnection = value; } }
		public NetConnection RPCNetConnection;

		AuthResponse AuthResponse;

		#endregion

		public RPCService(ConnectionProfile connectionData, ConnectCallbackType connectCallback, DisconnectCallbackType disconnectCallback = null, NetStatusCallbackType netStatusCallback = null)
		{
			ConnectionData = connectionData;
			ConnectCallback = connectCallback;
			DisconnectCallback = disconnectCallback;
			NetStatusCallback = netStatusCallback;

            /** Create service objects to call RPC functions */
            game = new GameService(this);
            matchmaker = new MatchmakerService(this);
            summoner = new SummonerService(this);
		}

		public void Connect()
		{
			//The HTTPS authentication is currently blocking so just create a new thread to make it non-blocking (although it is sub-optimal)
			Thread connectThread = new Thread(ConnectThread);
			connectThread.Start();
		}

		public void Disconnect()
		{
			NetConnection.Close();
		}

		void ConnectThread()
		{
			try
			{
				AuthService authService = new AuthService(ConnectionData.Region.LoginQueueURL, ConnectionData.Proxy.LoginQueueProxy);
				// Get an Auth token (Dumb, assumes no queueing, blocks)
				AuthResponse = authService.Authenticate(ConnectionData.User, ConnectionData.Password);
                Console.WriteLine("Reply: " + AuthResponse.Status + " " + AuthResponse.Rate);
			}
			catch (WebException exception)
			{
				ConnectCallback(new RPCConnectResult(exception));
				return;
			}

			// Initialise our RTMPS connection
			RPCNetConnection = new NetConnection();
			RPCNetConnection.Proxy = ConnectionData.Proxy.RTMPProxy;

			// We should use AMF3 to behave as closely to the client as possible.
			RPCNetConnection.ObjectEncoding = ObjectEncoding.AMF3;

			// Setup handlers for different network events.
			RPCNetConnection.OnConnect += new ConnectHandler(NetConnectionOnConnect);
			RPCNetConnection.OnDisconnect += new DisconnectHandler(NetConnectionOnDisconnect);
			RPCNetConnection.NetStatus += new NetStatusHandler(NetConnectionNetStatus);

			// Connect to the RTMPS server
			RPCNetConnection.Connect(ConnectionData.Region.RPCURL);
		}

		#region Net connection state handlers

		void NetConnectionOnConnect(object sender, EventArgs eventArguments)
		{
			/// TODO: Check if there was a problem connecting

			// Now that we are connected call the remote login function
			AuthenticationCredentials authenticationCredentials = new AuthenticationCredentials();
			authenticationCredentials.authToken = AuthResponse.Token;

			authenticationCredentials.clientVersion = ConnectionData.Authentication.ClientVersion;
			authenticationCredentials.domain = ConnectionData.Authentication.Domain;
			authenticationCredentials.ipAddress = ConnectionData.Authentication.IPAddress;
			authenticationCredentials.locale = ConnectionData.Authentication.Locale;
			authenticationCredentials.oldPassword = null;
			authenticationCredentials.partnerCredentials = null;
			authenticationCredentials.securityAnswer = null;
			authenticationCredentials.password = ConnectionData.Password;
			authenticationCredentials.username = ConnectionData.User;

			// Add some default headers
			RPCNetConnection.AddHeader(MessageBase.RequestTimeoutHeader, false, 60);
			RPCNetConnection.AddHeader(MessageBase.FlexClientIdHeader, false, Guid.NewGuid().ToString());
			RPCNetConnection.AddHeader(MessageBase.EndpointHeader, false, EndpointString);

			RPCNetConnection.Call(EndpointString, "loginService", null, "login", new Responder<Session>(OnLogin, OnLoginFault), authenticationCredentials);
		}

		void NetConnectionOnDisconnect(object sender, EventArgs eventArguments)
		{
			if (DisconnectCallback != null)
				DisconnectCallback();
		}

		void NetConnectionNetStatus(object sender, NetStatusEventArgs eventArguments)
		{
			//string level = eventArguments.Info["level"] as string;
			if (NetStatusCallback != null)
				NetStatusCallback(eventArguments);
		}
		#endregion

		#region LoL and Flex login
		
		void OnLogin(Session session)
		{
			// Client header should be set to the token we received from REST authentication
			RPCNetConnection.AddHeader(MessageBase.FlexClientIdHeader, false, session.token);

			// Create the command message which will do flex authentication
			CommandMessage message = new CommandMessage();
			message.operation = CommandMessage.LoginOperation;
			message.body = Convert.ToBase64String(System.Text.ASCIIEncoding.ASCII.GetBytes(ConnectionData.User + ":" + session.token));
			message.clientId = RPCNetConnection.ClientId;
			message.correlationId = null;
			message.destination = "";
			message.messageId = Guid.NewGuid().ToString();

			// Perform flex authentication.
			RPCNetConnection.Call("auth", new Responder<string>(OnFlexLogin, OnFlexLoginFault), message);
		}

		void OnLoginFault(Fault fault)
		{
			ConnectCallback(new RPCConnectResult(RPCConnectResultType.LoginFault, fault));
		}

		void OnFlexLogin(string message)
		{
			ConnectCallback(new RPCConnectResult(message));
		}

		void OnFlexLoginFault(Fault fault)
		{
			ConnectCallback(new RPCConnectResult(RPCConnectResultType.FlexLoginFault, fault));
		}

		#endregion

		#region Internal RPC

		void Call<ResponderType>(string destination, string operation, Responder<ResponderType> responder, params object[] arguments)
		{
			if (RPCNetConnection == null)
				throw new RPCNotConnectedException("Connection has not been initialised yet");
			RPCNetConnection.Call(EndpointString, destination, null, operation, responder, arguments);
		}

		void GetSummonerByNameInternal(Responder<PublicSummoner> responder, object[] arguments)
		{
			Call(SummonerService, "getSummonerByName", responder, arguments);
		}

		void GetRecentGamesInternal(Responder<RecentGames> responder, object[] arguments)
		{
			Call(PlayerStatsService, "getRecentGames", responder, arguments);
		}

		void GetAllPublicSummonerDataByAccountInternal(Responder<AllPublicSummonerDataDTO> responder, object[] arguments)
		{
			Call(SummonerService, "getAllPublicSummonerDataByAccount", responder, arguments);
		}

		void GetAllSummonerDataByAccountInternal(Responder<AllSummonerData> responder, object[] arguments)
		{
			Call(SummonerService, "getAllSummonerDataByAccount", responder, arguments);
		}

		void GetSummonerNamesInternal(Responder<List<string>> responder, object[] arguments)
		{
			Call(SummonerService, "getSummonerNames", responder, arguments);
		}

		void RetrievePlayerStatsByAccountIDInternal(Responder<PlayerLifeTimeStats> responder, object[] arguments)
		{
			Call(PlayerStatsService, "retrievePlayerStatsByAccountId", responder, arguments);
		}

		void GetAggregatedStatsInternal(Responder<AggregatedStats> responder, object[] arguments)
		{
			Call(PlayerStatsService, "getAggregatedStats", responder, arguments);
		}

		void FindPlayerInternal(Responder<PlayerDTO> responder, object[] arguments)
		{
			Call(SummonerTeamService, "findPlayer", responder, arguments);
		}

		//This call is not exposed to the outside
		void GetLoginDataPacketForUserInternal(Responder<LoginDataPacket> responder)
		{
			Call( ClientFacadeService, "getLoginDataPacketForUser", responder, new object[] {});
		}

		#endregion

		#region Non-blocking RPC

		public void GetSummonerByNameAsync(string name, Responder<PublicSummoner> responder)
		{
			GetSummonerByNameInternal(responder, new object[] { name });
		}

		public void GetRecentGamesAsync(long accountID, Responder<RecentGames> responder)
		{
			GetRecentGamesInternal(responder, new object[] { accountID });
		}

		public void GetAllPublicSummonerDataByAccountAsync(long accountID, Responder<AllPublicSummonerDataDTO> responder)
		{
			GetAllPublicSummonerDataByAccountInternal(responder, new object[] { accountID });
		}

		public void GetAllSummonerDataByAccountAsync(long accountID, Responder<AllSummonerData> responder)
		{
			GetAllSummonerDataByAccountInternal(responder, new object[] { accountID });
		}

		public void GetSummonerNamesAsync(List<long> summonerIDs, Responder<List<string>> responder)
		{
			GetSummonerNamesInternal(responder, new object[] { summonerIDs });
		}

		public void RetrievePlayerStatsByAccountIDAsync(long accountID, string season, Responder<PlayerLifeTimeStats> responder)
		{
			RetrievePlayerStatsByAccountIDInternal(responder, new object[] { accountID, season });
		}

		public void GetAggregatedStatsAsync(long accountID, string gameMode, string season, Responder<AggregatedStats> responder)
		{
			GetAggregatedStatsInternal(responder, new object[] { accountID, gameMode, season });
		}

		public void FindPlayerAsync(long summonerID, Responder<PlayerDTO> responder)
		{
			FindPlayerInternal(responder, new object[] { summonerID });
		}

		#endregion

		#region Blocking RPC

		public PublicSummoner GetSummonerByName(string name)
		{
			return (new InternalCallContext<PublicSummoner>(GetSummonerByNameInternal, new object[] { name })).Execute();
		}

		public RecentGames GetRecentGames(long accountID)
		{
			return (new InternalCallContext<RecentGames>(GetRecentGamesInternal, new object[] { accountID })).Execute();
		}

		public AllPublicSummonerDataDTO GetAllPublicSummonerDataByAccount(long accountID)
		{
			return (new InternalCallContext<AllPublicSummonerDataDTO>(GetAllPublicSummonerDataByAccountInternal, new object[] { accountID })).Execute();
		}

        // z
		//I don't understand how this one works anymore, always returns null for me
		public AllSummonerData GetAllSummonerDataByAccount(long accountID)
		{
			return (new InternalCallContext<AllSummonerData>(GetAllSummonerDataByAccountInternal, new object[] { accountID })).Execute();
		}

		public List<string> GetSummonerNames(List<long> summonerIDs)
		{
			return (new InternalCallContext<List<string>>(GetSummonerNamesInternal, new object[] { summonerIDs })).Execute();
		}

		public PlayerLifeTimeStats RetrievePlayerStatsByAccountID(long accountID, string season)
		{
			return (new InternalCallContext<PlayerLifeTimeStats>(RetrievePlayerStatsByAccountIDInternal, new object[] { accountID, season })).Execute();
		}

		public AggregatedStats GetAggregatedStats(long accountID, string gameMode, string season)
		{
			return (new InternalCallContext<AggregatedStats>(GetAggregatedStatsInternal, new object[] { accountID, gameMode, season })).Execute();
		}

		public PlayerDTO FindPlayer(int summonerID)
		{
			try
			{
				return (new InternalCallContext<PlayerDTO>(FindPlayerInternal, new object[] { summonerID })).Execute();
			}
			catch (RPCException)
			{
				//This just means that the summoner ID was invalid
				return null;
			}
		}

		#endregion
	}
}
