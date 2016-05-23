﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using libflist.Events;
using libflist.FChat.Events;
using libflist.FChat.Util;
using libflist.JSON.Responses;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using WebSocketSharp;
using libflist.FChat.Commands;

namespace libflist.FChat
{
	public sealed partial class FChatConnection : IDisposable
	{
		public sealed class KnownChannel
		{
			public string ID { get; set; }
			public string Title { get; set; }
			public ChannelMode Mode { get; set; }
			public int UserCount { get; set; }

			public bool Official { get { return ID == Title; } }
		}

		static readonly TimeSpan OFFICIAL_TIMEOUT = TimeSpan.FromMinutes(5);
		static readonly TimeSpan PRIVATE_TIMEOUT = TimeSpan.FromMinutes(1);

		public static readonly Uri LiveServerEndpoint = new Uri("wss://chat.f-list.net:9799");
		public static readonly Uri TestingServerEndpoint = new Uri("wss://chat.f-list.net:8799");
#if DEBUG
		public Uri Endpoint = TestingServerEndpoint;
#else
		public Uri Endpoint = LiveServerEndpoint;
#endif

		DateTime _LastPublicUpdate;
		DateTime _LastPrivateUpdate;
		List<KnownChannel> _OfficialChannels;
		List<KnownChannel> _PrivateChannels;

		List<Channel> _Channels;
		List<Character> _Characters;

		readonly Dictionary<string, EventHandler<Command>> _Handlers;

		readonly ServerVariables _Variables;
		WebSocket _Connection;
		bool _Identified = false;
		string _User;
		string _Character;

		public bool AutoPing { get; set; }
		public bool AutoReconnect { get; set; }
		public bool AutoUpdate { get; set; }

		public IReadOnlyDictionary<string, EventHandler<Command>> MessageHandlers { get { return _Handlers; } }
		public ServerVariables Variables { get { return _Variables; } }
		public TicketResponse Ticket { get; set; }
		public DateTime TicketTimestamp { get; set; }

		public IEnumerable<KnownChannel> AllKnownChannels { get { return _OfficialChannels.Concat(_PrivateChannels); } }
		public IReadOnlyCollection<KnownChannel> OfficialChannels
		{
			get
			{
				if (AutoUpdate && DateTime.Now > _LastPublicUpdate + OFFICIAL_TIMEOUT)
				{
					_LastPublicUpdate = DateTime.Now;
					RequestCommand<Commands.Server.ChatGetPublicChannels>(new Commands.Client.Global.GetPublicChannelsCommand());
					return _OfficialChannels;
				}
				return _OfficialChannels;
			}
		}
		public IReadOnlyCollection<KnownChannel> PrivateChannels
		{
			get
			{
				if (AutoUpdate && DateTime.Now > _LastPrivateUpdate + PRIVATE_TIMEOUT)
				{
					_LastPrivateUpdate = DateTime.Now;
					RequestCommand<Commands.Server.PrivateChannelListReply>(new Commands.Client.Global.GetPrivateChannelsCommand());
					return _PrivateChannels;
				}
				return _PrivateChannels;
			}
		}

		public IEnumerable<Channel> ActiveChannels { get { return _Channels; } }

		// Server events
		public event EventHandler OnConnected;
		public event EventHandler OnDisconnected;
		public event EventHandler OnIdentified;

		// Message events
		public event EventHandler OnError;
		public event EventHandler<CommandEventArgs> OnErrorMessage;
		public event EventHandler<CommandEventArgs> OnRawMessage;
		public event EventHandler<CommandEventArgs> OnSYSMessage;

		// Channel list events
		public event EventHandler OnOfficialListUpdate;
		public event EventHandler OnPrivateListUpdate;

		// OP events
		public event EventHandler<CharacterEntryEventArgs> OnOPAdded;
		public event EventHandler<CharacterEntryEventArgs> OnOPRemoved;

		// Channel entry events
		public event EventHandler<ChannelEntryEventArgs> OnChannelJoin;
		public event EventHandler<ChannelEntryEventArgs> OnChannelLeave;

		// Channel user entry events
		public event EventHandler<ChannelUserEntryEventArgs> OnChannelUserJoin;
		public event EventHandler<ChannelUserEntryEventArgs> OnChannelUserLeave;

		// Channel admin events
		public event EventHandler<ChannelAdminActionEventArgs> OnChannelUserKicked;
		public event EventHandler<ChannelAdminActionEventArgs> OnChannelUserBanned;
		public event EventHandler<ChannelAdminActionEventArgs> OnChannelUserUnbanned;
		public event EventHandler<ChannelAdminActionEventArgs> OnChannelUserTimedout;

		// Channel status events
		public event EventHandler<ChannelEntryEventArgs<string>> OnChannelDescriptionChange;
		public event EventHandler<ChannelEntryEventArgs<ChannelMode>> OnChannelModeChange;
		public event EventHandler<ChannelEntryEventArgs<Character>> OnChannelOwnerChange;
		public event EventHandler<ChannelEntryEventArgs<ChannelStatus>> OnChannelStatusChange;
		// public event EventHandler<ChannelEntryEventArgs<string>> OnChannelTitleChange;

		// Channel OP events
		public event EventHandler<ChannelUserEntryEventArgs> OnChannelOPAdded;
		public event EventHandler<ChannelUserEntryEventArgs> OnChannelOPRemoved;

		// Channel message events
		public event EventHandler<ChannelUserMessageEventArgs> OnChannelChatMessage;
		public event EventHandler<ChannelUserMessageEventArgs> OnChannelLFRPMessage;
		public event EventHandler<ChannelUserMessageEventArgs> OnChannelRollMessage;
		public event EventHandler<ChannelEntryEventArgs<string>> OnChannelSYSMessage;

		// Character entry events
		public event EventHandler<CharacterEntryEventArgs> OnCharacterOnline;
		public event EventHandler<CharacterEntryEventArgs> OnCharacterOffline;

		// Character list events
		public event EventHandler OnFriendsListUpdate;
		public event EventHandler OnIgnoreListUpdate;

		// Character admin events
		public event EventHandler<AdminActionEventArgs> OnCharacterKicked;
		public event EventHandler<AdminActionEventArgs> OnCharacterBanned;
		public event EventHandler<AdminActionEventArgs> OnCharacterUnbanned;
		public event EventHandler<AdminActionEventArgs> OnCharacterTimedout;

		// Character status events
		public event EventHandler<CharacterEntryEventArgs<CharacterStatus>> OnCharacterStatusChange;
		public event EventHandler<CharacterEntryEventArgs<TypingStatus>> OnCharacterTypingChange;

		// Character message events
		public event EventHandler<CharacterMessageEventArgs> OnCharacterChatMessage;


		public FChatConnection()
		{
			_OfficialChannels = new List<KnownChannel>();
			_PrivateChannels = new List<KnownChannel>();

			_Channels = new List<Channel>();
			_Characters = new List<Character>();
			_Variables = new ServerVariables();

			_Handlers = new Dictionary<string, EventHandler<Command>>();
			foreach (var token in CommandParser.ImplementedReplies)
				_Handlers.Add(token, null);

			AddDefaultHandlers();
		}

		public void Dispose()
		{
			try
			{
				if (_Connection != null)
					_Connection.Close();
			}
			catch(Exception)
			{ }

			_Variables.Clear();
			_Channels = null;
			_Characters = null;

			_Character = null;
			Ticket = null;
			_User = null;
			_Connection = null;
		}

		public void SendCommand(Commands.Command command)
		{
			if (command.Source != Commands.CommandSource.Client)
				throw new ArgumentException("Command source is invalid.", nameof(command));

			lock (_Connection)
				_Connection.Send(command.Serialize());
		}
		public async Task<bool> SendCommandAsync(Commands.Command command)
		{
			if (command.Source != Commands.CommandSource.Client)
				throw new ArgumentException("Command source is invalid.", nameof(command));

			bool result = false;
			var ev = new AsyncAutoResetEvent();

			_Connection.SendAsync(command.Serialize(), (r) => {
				result = r;
				ev.Set();
			});

			await ev.WaitAsync();

			return result;
		}
		public T RequestCommand<T>(Command query, int msTimeout = 250) where T : Command
		{
			var att = query.GetType().GetCustomAttribute<CommandAttribute>();
			if (att.Response != ResponseType.Default || att.ResponseToken == "SYS")
				throw new ArgumentException("Can only use queries with proper response information", nameof(query));
			var ratt = typeof(T).GetCustomAttribute<ReplyAttribute>();
			if (ratt == null || att.ResponseToken != ratt.Token)
				throw new ArgumentException("Provided respose type is not a valid response to the query");

			var ev = new AutoResetEvent(false);

			Command reply = null;
			var waiter = new EventHandler<Command>((_, e) =>
			{
				reply = e;
				ev.Set();
			});
			_Handlers[ratt.Token] += waiter;

			SendCommand(query);
			var successful = ev.WaitOne(msTimeout);

			_Handlers[ratt.Token] -= waiter;

			return successful ? reply as T : null;
		}
		public async Task<T> RequestCommandAsync<T>(Command query) where T : Command
		{
			var att = query.GetType().GetCustomAttribute<CommandAttribute>();
			if (att.Response != ResponseType.Default || att.ResponseToken == "SYS")
				throw new ArgumentException("Can only use queries with proper response information", nameof(query));
			var ratt = typeof(T).GetCustomAttribute<ReplyAttribute>();
			if (ratt == null || att.ResponseToken != ratt.Token)
				throw new ArgumentException("Provided respose type is not a valid response to the query");
			
			var ev = new AsyncAutoResetEvent();

			Command reply = null;
			var waiter = new EventHandler<Command>((_, e) =>
			{
				reply = e;
				ev.Set();
			});
			_Handlers[ratt.Token] += waiter;

			await SendCommandAsync(query);
			await ev.WaitAsync();

			_Handlers[ratt.Token] -= waiter;

			return reply as T;
		}


		// TODO: Split connection into several steps;
		//   bool FChat.AquireTicket(string User, string Password);
		//   bool FChat.Ticket.IsValid { get; }
		//   void FChat.Connect();
		//   void FChat.Login(string Character);
		// For instance.

		public void Connect(string User, string Password, bool UseTicket = false)
		{
			if (_Connection != null)
				Disconnect();

			if (User == null)
				throw new ArgumentNullException(nameof(User));

			if (!UseTicket)
			{
				if (Password == null)
					throw new ArgumentNullException(nameof(Password));

				using (var jr = new JSON.Request(JSON.Endpoint.Path.Ticket))
				{
					jr.Data = new Dictionary<string, string> {
						{ "account", User },
						{ "password", Password }
					};

					Ticket = jr.Get<TicketResponse>() as TicketResponse;
					if (!Ticket.Successful)
						return;

					TicketTimestamp = DateTime.Now;
				}
			}

			_User = User;
			_Variables.Clear();

			_Connection = new WebSocket(Endpoint.AbsoluteUri);
			lock (_Connection)
			{
				_Connection.OnClose += _Connection_OnClose;
				_Connection.OnError += _Connection_OnError;
				_Connection.OnMessage += _Connection_OnMessage;
				_Connection.OnOpen += _Connection_OnOpen;

				_Connection.Connect();
			}
		}

		public void Disconnect()
		{
			if (_Connection == null)
				return;

			lock (_Connection)
			{
				try
				{
					_Connection.Close();
				}
				catch (Exception)
				{}
			}

			_Connection = null;
			_Identified = false;
		}

		public void Reconnect(bool AutoLogin = true)
		{
			if (Ticket == null || _User == null)
				throw new ArgumentNullException(nameof(Ticket));

			if (DateTime.Now - TicketTimestamp > TimeSpan.FromHours(24))
				throw new ArgumentException("Ticket has timed out, reconnect is not possible");

			_Variables.Clear();
			lock (_Connection)
			{
				_Connection.Connect();

				if (AutoLogin)
					SendCommand(new Commands.Client.Connection.IdentifyCommand {
						Account = _User,
						Ticket = Ticket.Ticket,
						Character = _Character
					});
			}
		}

		public void Login(string Character)
		{
			if (Character == null)
				throw new ArgumentNullException(nameof(Character));
			if (!Ticket.Characters.Contains(Character))
				throw new ArgumentException("Unknown character specified", nameof(Character));

			lock (_Connection)
			{
				if (_Connection == null)
					throw new Exception("Not connected.");

				if (_Identified)
					return;

				_Character = Character;
				SendCommand(new Commands.Client.Connection.IdentifyCommand
				{
					Account = _User,
					Ticket = Ticket.Ticket,
					Character = _Character
				});
			}
		}

		public Character LocalCharacter
		{
			get { return GetCharacter(_Character); }
		}

		public Channel GetChannel(string ID)
		{
			lock (_Channels)
				return _Channels.FirstOrDefault(c => c.ID.Equals(ID, StringComparison.CurrentCultureIgnoreCase));
		}
		public Channel GetOrCreateChannel(string ID)
		{
			Channel chan;
			lock (_Channels)
			{
				chan = _Channels.FirstOrDefault(c => c.ID.Equals(ID, StringComparison.CurrentCultureIgnoreCase));
				if (chan != null)
					return chan;

				chan = new Channel(this, ID, ID);
				_Channels.Add(chan);
			}
			return chan;
		}
		public Channel GetOrJoinChannel(string ID)
		{
			lock (_Channels)
			{
				var chan = _Channels.FirstOrDefault(c => c.ID.Equals(ID, StringComparison.CurrentCultureIgnoreCase));
				if (chan != null && chan.Joined)
					return chan;

				var reply = RequestCommand<Commands.Server.Channel.JoinReply>(new Commands.Client.Channel.JoinCommand { Channel = ID });

				if (chan == null)
				{
					chan = new Channel(this, ID, reply.Title);
					_Channels.Add(chan);
				}
				return chan;
			}
		}
		public Channel GetOrJoinChannelDelayed(string ID)
		{
			Channel chan;
			lock (_Channels)
			{ 
				chan = _Channels.FirstOrDefault(c => c.ID.Equals(ID, StringComparison.CurrentCultureIgnoreCase));
			
				if (chan != null && chan.Joined)
					return chan;

#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
				SendCommandAsync(new Commands.Client.Channel.JoinCommand { Channel = ID });
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed

				if (chan == null)
				{
					chan = new Channel(this, ID, ID);
					_Channels.Add(chan);
				}
				return chan;
			}
		}
		public Channel JoinChannel(string ID)
		{
			lock (_Channels)
			{
				var chan = _Channels.FirstOrDefault(c => c.ID.Equals(ID, StringComparison.CurrentCultureIgnoreCase));
				if (chan != null && chan.Joined)
					return chan;

				var reply = RequestCommand<Commands.Server.Channel.JoinReply>(new Commands.Client.Channel.JoinCommand { Channel = ID });

				if (chan == null)
				{
					chan = new Channel(this, ID, reply.Title);
					_Channels.Add(chan);
				}
				return chan;
			}
		}
		public Character GetCharacter(string Name)
		{
			lock (_Characters)
			{
				return _Characters.FirstOrDefault(c => c.Name.Equals(Name, StringComparison.CurrentCultureIgnoreCase));
			}
		}
		public Character GetOrCreateCharacter(string Name)
		{
			lock (_Characters)
			{
				var charac = _Characters.FirstOrDefault(c => c.Name.Equals(Name, StringComparison.CurrentCultureIgnoreCase));
				if (charac != null)
					return charac;

				charac = new Character(this, Name);
				_Characters.Add(charac);

				return charac;
			}
		}


		void _Connection_OnOpen(object sender, EventArgs e)
		{
			OnConnected?.Invoke(this, e);
		}

		void _Connection_OnMessage(object sender, MessageEventArgs e)
		{
			var token = e.Data.Substring(0, 3);
			var data = e.Data.Substring(4);

			var reply = Commands.CommandParser.ParseReply(token, data, true);

			_HandleMessage(reply);
		}

		void _Connection_OnError(object sender, ErrorEventArgs e)
		{
			OnError?.Invoke(this, e);

			_HandleMessage(new Commands.Meta.FailedReply
			{
				Data = e.Message,
				Exception = e.Exception
			});

			Disconnect();
		}

		void _Connection_OnClose(object sender, CloseEventArgs e)
		{
			OnDisconnected?.Invoke(this, e);

			Disconnect();

			// TODO: Count reconnect attempts.
			if (AutoReconnect && !e.WasClean && !string.IsNullOrEmpty(_Character))
				Task.Delay(15000).ContinueWith(_ => Reconnect());
		}

		void _HandleMessage(Command cmd)
		{
			OnRawMessage?.Invoke(this, new CommandEventArgs(cmd));

			if (_Handlers.ContainsKey(cmd.Token))
				_Handlers[cmd.Token]?.Invoke(this, cmd);
			else
				Debug.WriteLine(string.Format("Unhandled command; {0}", cmd.Token));
			
			Channel disposed = null;
			if (cmd is Command.IChannelCommand &&
				!string.IsNullOrEmpty((cmd as Command.IChannelCommand).Channel))
			{
				var channel = (cmd as Command.IChannelCommand).Channel;

				Channel channelObj;
				lock (_Channels)
				{
					channelObj = _Channels.FirstOrDefault(c => c.ID == channel);
					if (channelObj == null)
					{
						channelObj = new Channel(this, channel, channel);
						_Channels.Add(channelObj);
					}
				}

				channelObj.PushCommand(cmd);

				if (channelObj.IsDisposed)
					disposed = channelObj;
			}
			
			lock (_Channels)
				if (disposed != null)
					_Channels.Remove(disposed);
		}
	}

}
