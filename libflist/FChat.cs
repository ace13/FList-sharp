﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using libflist.Connection;
using libflist.Connection.Commands;
using libflist.Connection.Types;
using libflist.Events;
using libflist.JSON.Responses;
using libflist.Util.Converters;
using System.Diagnostics;

namespace libflist
{
	public sealed class FChat : IDisposable
	{
		public sealed class UserAccount : IDisposable
		{
			TicketResponse _Ticket;
			string _User, _Char;

			internal UserAccount(TicketResponse ticket, string user, string charact)
			{
				_Ticket = ticket;
				_User = user;
				_Char = charact;
			}
			public void Dispose()
			{

			}

			public string UserName
			{
				get { return _User; }
			}

			public string DefaultCharacter
			{
				get { return _Ticket.DefaultCharacter ?? _Ticket.Characters.First(); }
			}
			public IEnumerable<string> Characters
			{
				get { return _Ticket.Characters; }
			}
			public string CurrentCharacter
			{
				get { return _Char; }
			}

			public IEnumerable<Channel> Channels
			{
				get { throw new NotImplementedException(); }
			}
		}

		public sealed class KnownChannel
		{
			public string ID { get; set; }
			public string Title { get; set; }
			public ChannelMode Mode { get; set; }

			public int Count { get; set; }

			public bool Official { get { return ID == Title; } }
		}

		static readonly TimeSpan OFFICIAL_TIMEOUT = TimeSpan.FromMinutes(5);
		static readonly TimeSpan PRIVATE_TIMEOUT = TimeSpan.FromMinutes(1);

		DateTime _LastPublicUpdate;
		DateTime _LastPrivateUpdate;
		List<KnownChannel> _KnownChannels;
		List<Channel> _Channels;
		List<Character> _Characters;

		readonly ServerVariables _Variables;
		ChatConnection _Connection;
		string _User;
		string _Character;

		public ServerVariables Variables { get { return _Variables; } }
		public ChatConnection Connection { get { return _Connection; } }
		public TicketResponse Ticket { get; set; }
		public DateTime TicketTimestamp { get; set; }

		// TODO: Reload if data is stale.
		public IReadOnlyList<KnownChannel> KnownChannels { get { return _KnownChannels; } }

		public IEnumerable<KnownChannel> OfficialChannels
		{
			get
			{
				if (DateTime.Now - _LastPublicUpdate > OFFICIAL_TIMEOUT)
					SendCommand(new Connection.Commands.Client.Global.GetPublicChannelsCommand());

				return _KnownChannels.Where(c => c.Official);
			}
		}
		public IEnumerable<KnownChannel> PrivateChannels
		{
			get
			{
				if (DateTime.Now - _LastPrivateUpdate > PRIVATE_TIMEOUT)
					SendCommand(new Connection.Commands.Client.Global.GetPrivateChannelsCommand());

				return _KnownChannels.Where(c => !c.Official);
			}
		}

		public IEnumerable<Channel> JoinedChannels { get { return _Channels; } }

		public event EventHandler<CharacterEntryEventArgs> OnOnline; // JCH
		public event EventHandler<CharacterEntryEventArgs> OnOffline; // LCH

		public event EventHandler<ChannelEntryEventArgs<IReadOnlyList<KnownChannel>>> OnPublicChanListUpdate; // CHA
		public event EventHandler<ChannelEntryEventArgs<IReadOnlyList<KnownChannel>>> OnPrivateChanListUpdate; // ORS

		public event EventHandler<ChannelEntryEventArgs> OnJoinChannel; // JCH
		public event EventHandler<ChannelEntryEventArgs> OnLeaveChannel; // LCH

		public event EventHandler<CharacterEntryEventArgs<CharacterStatus>> OnStatusChange;
		public event EventHandler<CharacterEntryEventArgs<TypingStatus>> OnTypingChange;

		public event EventHandler<CharacterEntryEventArgs> OnGivenOP; // COA
		public event EventHandler<CharacterEntryEventArgs> OnRemovedOP; // COR

		/*
		public event EventHandler<AdminActionEventArgs> OnKicked; // CKU
		public event EventHandler<AdminActionEventArgs> OnBanned; // CBU
		public event EventHandler<AdminActionEventArgs> OnUnbanned; // CUB
		public event EventHandler<AdminActionEventArgs> OnTimedout; // CTU
		*/

		public event EventHandler<CharacterMessageEventArgs> OnPrivateMessage; // PRI
		public event EventHandler<ChannelEntryEventArgs<string>> OnErrorMessage; // ERR
		public event EventHandler<ChannelEntryEventArgs<string>> OnSYSMessage; // SYS

		public FChat()
		{
			_KnownChannels = new List<KnownChannel>();
			_Channels = new List<Channel>();
			_Characters = new List<Character>();
			_Variables = new ServerVariables();

			_Connection = new ChatConnection();

			_Connection.OnConnected += _Connection_OnConnected;
			_Connection.OnDisconnected += _Connection_OnDisconnected;
			_Connection.OnIdentified += _Connection_OnIdentified;

			_Connection.OnReceivedCommand += _Connection_OnReceivedCommand;
		}

		public void Dispose()
		{
			_Connection.Disconnect();

			_Variables.Clear();
			_KnownChannels = null;
			_Channels = null;
			_Characters = null;

			_Character = null;
			Ticket = null;
			_User = null;
			_Connection = null;
		}

		// TODO: Split connection into several steps;
		//   bool FChat.AquireTicket(string User, string Password);
		//   bool FChat.Ticket.IsValid { get; }
		//   void FChat.Connect();
		//   void FChat.Login(string Character);
		// For instance.

		public void Connect(string User, string Password, bool UseTicket = false)
		{
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

			lock (_Connection)
			{
				_Connection.Connect();
			}
		}

		public void Disconnect()
		{
			lock (_Connection)
				_Connection.Disconnect();
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
					_Connection.Identify(_User, Ticket.Ticket, _Character);
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
				if (!_Connection.Connected)
					throw new Exception("Not connected.");

				if (_Connection.Identified)
					return;

				_Character = Character;
				_Connection.Identify(_User, Ticket.Ticket, _Character);
			}
		}

		public void SendCommand(Command cmd)
		{
			if (cmd.GetType().GetCustomAttribute<ReplyAttribute>() != null)
				throw new ArgumentException("Can't send server replies", nameof(cmd));

			if (cmd is Connection.Commands.Client.Global.GetPublicChannelsCommand)
				_LastPublicUpdate = DateTime.Now;
			if (cmd is Connection.Commands.Client.Global.GetPrivateChannelsCommand)
				_LastPrivateUpdate = DateTime.Now;

			lock (_Connection)
				_Connection.SendCommand(cmd);
		}


		public UserAccount User
		{
			get { return new UserAccount(Ticket, _User, _Character); }
		}

		public Character LocalCharacter
		{
			get { return GetCharacter(_Character); }
		}

		public Channel GetOrJoinChannel(string ID)
		{
			lock (_Channels)
			{
				var chan = _Channels.FirstOrDefault(c => c.ID.ToLower() == ID.ToLower());
				if (chan != null)
					return chan;

				chan = new Channel(this, ID, ID);
				SendCommand(new Connection.Commands.Client.Channel.JoinCommand
				{
					Channel = ID
				});

				_Channels.Add(chan);
				return chan;
			}
		}
		public Character GetCharacter(string Name)
		{
			lock (_Characters)
			{
				return _Characters.FirstOrDefault(c => c.Name.ToLower() == Name.ToLower());
			}
		}
		public Character GetOrCreateCharacter(string Name)
		{
			lock (_Characters)
			{
				var charac = _Characters.FirstOrDefault(c => c.Name.ToLower() == Name.ToLower());
				if (charac != null)
					return charac;

				charac = new Character(this, Name);

				_Characters.Add(charac);
				return charac;
			}
		}

		void _Connection_OnConnected(object sender, EventArgs e)
		{

		}

		void _Connection_OnDisconnected(object sender, EventArgs e)
		{
			// TODO: Count reconnect attempts.
			if (!string.IsNullOrEmpty(_Character))
				Task.Delay(15000).ContinueWith(_ => Reconnect());
		}

		void _Connection_OnIdentified(object sender, EventArgs e)
		{
			_Connection.SendCommand(new Connection.Commands.Client.Server.UptimeCommand());
		}

		void _Connection_OnReceivedCommand(object sender, Connection.Util.CommandEventArgs e)
		{
			bool handled = false;

			if (e.Command is Command.IChannelCommand &&
				!string.IsNullOrEmpty((e.Command as Command.IChannelCommand).Channel))
			{
				var channel = (e.Command as Command.IChannelCommand).Channel;

				Channel channelObj;
				lock (_Channels)
				{
					channelObj = _Channels.FirstOrDefault(c => c.ID == channel);
					if (channelObj == null)
					{
						channelObj = new Channel(this, channel, channel);
						_Channels.Add(channelObj);

						if (OnJoinChannel != null)
							OnJoinChannel(this, new ChannelEntryEventArgs(channelObj, e.Command));
					}
				}

				handled |= channelObj.PushCommand(e.Command);

				if (channelObj.IsDisposed)
				{
					lock (_Channels)
						_Channels.Remove(channelObj);

					if (OnLeaveChannel != null)
						OnLeaveChannel(this, new ChannelEntryEventArgs(channelObj, e.Command));
				}
			}

			if (!handled)
			{
				switch (e.Command.Token)
				{
					case "!!!":
						{
							handled = true;

							var err = e.Command as Connection.Commands.Meta.FailedReply;

							Debug.WriteLine("Invalid command recieved: {0} - {2}\n{1}", err.CMDToken, err.Data, err.Exception);
						}
						break;

					case "???":
						{
							handled = true;

							var err = e.Command as Connection.Commands.Meta.UnknownReply;

							Debug.WriteLine("Unknown command recieved: {0}\n{1}", err.CMDToken, err.Data);
						}
						break;

					case "ADL":
						{
							handled = true;

							var adl = e.Command as Connection.Commands.Server.ChatOPList;

							// TODO: Handle OP list properly.
							Debug.WriteLine($"Recieved OP list with {adl.OPs.Length} entries.");
						}
						break;

					case "AOP":
						{
							handled = true;

							var aop = e.Command as Connection.Commands.Server.ChatMakeOP;
							var character = GetCharacter(aop.Character);

							if (OnGivenOP != null)
								OnGivenOP(this, new CharacterEntryEventArgs(character, aop));
						}
						break;
						
					case "CHA":
						{
							handled = true;

							var cha = e.Command as Connection.Commands.Server.ChatGetPublicChannels;

							var oldList = _KnownChannels;
							_KnownChannels = new List<KnownChannel>();
							_KnownChannels.AddRange(oldList.Where(c => !c.Official));
							_KnownChannels.AddRange(cha.Channels.Select(c => new KnownChannel { Count = c.Count, ID = c.Name, Title = c.Name, Mode = c.Mode }));

							if (OnPublicChanListUpdate != null)
								OnPublicChanListUpdate(this, new ChannelEntryEventArgs<IReadOnlyList<KnownChannel>>(_KnownChannels, cha) { Old = oldList });
						}
						break;

					case "CON":
						{
							handled = true;

							var con = e.Command as Connection.Commands.Server.ServerConnected;

							_Variables.SetVariable("__connected", con.ConnectedUsers);
						}
						break;

					case "DOP":
						{
							handled = true;

							var dop = e.Command as Connection.Commands.Server.ChatRemoveOP;
							var character = GetCharacter(dop.Character);

							if (OnRemovedOP != null)
								OnRemovedOP(this, new CharacterEntryEventArgs(character, dop));
						}
						break;

					case "ERR":
						{
							handled = true;

							var err = e.Command as Connection.Commands.Server.ChatError;

							if (OnErrorMessage != null)
								OnErrorMessage(this, new ChannelEntryEventArgs<string>(err.Error, err));
						}
						break;

					case "FLN":
						{
							handled = true;

							var fln = e.Command as Connection.Commands.Server.Character.OfflineReply;
							var character = GetCharacter(fln.Character);
							if (character == null)
							{
								character = new Character(this, fln.Character);

								if (OnOffline != null)
									OnOffline(this, new CharacterEntryEventArgs(character, fln));

								break;
							}

							if (OnOffline != null)
								OnOffline(this, new CharacterEntryEventArgs(character, fln));

							lock (_Characters)
							{
								_Characters.Remove(character);
							}

							foreach (var chan in _Channels.Where(c => c.Characters.Contains(character)))
								chan.PushCommand(new Connection.Commands.Server.Channel.LeaveReply
								{
									Channel = chan.ID,
									Character = character.Name
								});
						}
						break;

					case "FRL":
						{
							handled = true;
							var frl = e.Command as Connection.Commands.Server.FriendListReply;

							Debug.WriteLine($"Recieved {frl.FriendsAndBookmarks.Length} friends and bookmarks");
						}
						break;

					case "HLO":
						{
							handled = true;

							var hlo = e.Command as Connection.Commands.Server.ServerHello;

							// TODO: Properly report server hello.
							Debug.WriteLine(hlo.Message);
						}
						break;

					case "IDN":
						{
							handled = true;

							var idn = e.Command as Connection.Commands.Server.ServerIdentify;

							// TODO: Handle identifying properly
							Debug.WriteLine($"Identified as {idn.Character}");
						}
						break;

					case "IGN":
						{
							handled = true;

							var ign = e.Command as Connection.Commands.Server.IgnoreListReply;

							// TODO: Handle ignores

							switch(ign.Action)
							{
								case libflist.Connection.Commands.Server.IgnoreListReply.IgnoreAction.Init:
									Debug.WriteLine($"Initial ignore list received. {ign.Characters.Length} entries.");
									break;

								case libflist.Connection.Commands.Server.IgnoreListReply.IgnoreAction.Add:
									Debug.WriteLine($"TODO: Add {ign.Character} to ignore list.");
									break;

								case libflist.Connection.Commands.Server.IgnoreListReply.IgnoreAction.Delete:
									Debug.WriteLine($"TODO: Remove {ign.Character} from ignore list.");
									break;
							}
						}
						break;

					case "NLN":
						{
							handled = true;

							var nln = e.Command as Connection.Commands.Server.Character.OnlineReply;

							var character = GetOrCreateCharacter(nln.CharacterName);
							character.Gender = nln.Gender;
							character.Status = nln.Status;

							if (OnOnline != null)
								OnOnline(this, new CharacterEntryEventArgs(character, nln));
						}
						break;

					case "LIS":
						{
							handled = true;

							var lis = e.Command as Connection.Commands.Server.UserListReply;

							foreach (var character in lis.CharacterData)
							{
								var charObj = GetOrCreateCharacter(character[0]);

								charObj.Gender = JsonEnumConverter.Convert<CharacterGender>(character[1]);
								charObj.Status = JsonEnumConverter.Convert<CharacterStatus>(character[2]);
								charObj.StatusMessage = character[3];
							}
						}
						break;

					case "ORS":
						{
							handled = true;

							var ors = e.Command as Connection.Commands.Server.PrivateChannelListReply;

							var oldList = _KnownChannels;
							_KnownChannels = new List<KnownChannel>();
							_KnownChannels.AddRange(oldList.Where(c => c.Official));
							_KnownChannels.AddRange(ors.Channels.Select(c => new KnownChannel { Count = c.Count, ID = c.ID, Title = c.Title }));

							if (OnPrivateChanListUpdate != null)
								OnPrivateChanListUpdate(this, new ChannelEntryEventArgs<IReadOnlyList<KnownChannel>>(_KnownChannels, ors) { Old = oldList });
						}
						break;

					case "PIN":
						{
							handled = true;

							_Connection.SendCommand(new Connection.Commands.Client.Server.PingCommand());
						}
						break;

					case "PRI":
						{
							handled = true;

							var pri = e.Command as Connection.Commands.Server.Character.SendMessageReply;
							var character = GetCharacter(pri.Character);

							character.IsTyping = false;

							if (OnPrivateMessage != null)
								OnPrivateMessage(this, new CharacterMessageEventArgs(character, pri.Message, pri));
						}
						break;

					case "STA":
						{
							handled = true;

							var sta = e.Command as Connection.Commands.Server.Character.StatusReply;
							var character = GetCharacter(sta.Character);

							character.Status = sta.Status;
							character.StatusMessage = sta.Message;

							if (OnStatusChange != null)
								OnStatusChange(this, new CharacterEntryEventArgs<CharacterStatus>(character, sta.Status, sta));
						}
						break;

					case "SYS":
						{
							handled = true;

							var sys = e.Command as Connection.Commands.Server.SysReply;

							if (OnSYSMessage != null)
								OnSYSMessage(this, new ChannelEntryEventArgs<string>(sys.Message, sys));
						}
						break;

					case "TPN":
						{
							handled = true;

							var tpn = e.Command as Connection.Commands.Server.Character.TypingReply;
							var character = GetCharacter(tpn.Character);

							character.IsTyping = tpn.Status == TypingStatus.Typing;

							if (OnTypingChange != null)
								OnTypingChange(this, new CharacterEntryEventArgs<TypingStatus>(character, tpn.Status, tpn));
						}
						break;

					case "UPT":
						{
							handled = true;

							var upt = e.Command as Connection.Commands.Server.ServerUptime;

							_Variables.SetVariable("__boot_time", upt.StartTime);
							_Variables.SetVariable("__users", upt.CurrentUsers);
							_Variables.SetVariable("__channels", upt.Channels);
							_Variables.SetVariable("__connections", upt.AcceptedConnections);
							_Variables.SetVariable("__peak", upt.PeakUsers);
						}
						break;

					case "VAR":
						{
							handled = true;

							var var = e.Command as Connection.Commands.Server.ServerVariable;

							_Variables.SetVariable(var.Name, var.Value);
						}
						break;
				}
			}

			if (!handled)
				Debug.WriteLine(string.Format("Unhandled command; {0}", e.Command.Token));
		}
	}
}