﻿using Discord.API;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Discord
{
	public struct ChannelPermissionsPair
	{
		public Channel Channel;
		public ChannelPermissions Permissions;

		public ChannelPermissionsPair(Channel channel)
		{
			Channel = channel;
			Permissions = new ChannelPermissions();
			Permissions.Lock();
        }
	}

	public class User : CachedObject<long>
	{
		internal struct CompositeKey : IEquatable<CompositeKey>
		{
			public long ServerId, UserId;
			public CompositeKey(long userId, long? serverId)
			{
				ServerId = serverId ?? 0;
				UserId = userId;
			}

			public bool Equals(CompositeKey other)
				=> UserId == other.UserId && ServerId == other.ServerId;
			public override int GetHashCode()
				=> unchecked(ServerId.GetHashCode() + UserId.GetHashCode() + 23);
		}

		internal static string GetAvatarUrl(long userId, string avatarId) => avatarId != null ? Endpoints.UserAvatar(userId, avatarId) : null;
		
		private ConcurrentDictionary<long, ChannelPermissionsPair> _permissions;
		private ServerPermissions _serverPermissions;

		/// <summary> Returns a unique identifier combining this user's id with its server's. </summary>
		internal CompositeKey UniqueId => new CompositeKey(_server.Id ?? 0, Id);
		/// <summary> Returns the name of this user on this server. </summary>
		public string Name { get; private set; }
		/// <summary> Returns a by-name unique identifier separating this user from others with the same name. </summary>
		public short Discriminator { get; private set; }
		/// <summary> Returns the unique identifier for this user's current avatar. </summary>
		public string AvatarId { get; private set; }
		/// <summary> Returns the URL to this user's current avatar. </summary>
		public string AvatarUrl => GetAvatarUrl(Id, AvatarId);
		/// <summary> Returns the datetime that this user joined this server. </summary>
		public DateTime JoinedAt { get; private set; }

		public bool IsSelfMuted { get; private set; }
		public bool IsSelfDeafened { get; private set; }
		public bool IsServerMuted { get; private set; }
		public bool IsServerDeafened { get; private set; }
		public bool IsServerSuppressed { get; private set; }
		public bool IsSpeaking { get; internal set; }
		public bool IsPrivate => _server.Id == null;

		public string SessionId { get; private set; }
		public string Token { get; private set; }

		/// <summary> Returns the id for the game this user is currently playing. </summary>
		public int? GameId { get; private set; }
		/// <summary> Returns the current status for this user. </summary>
		public UserStatus Status { get; private set; }
		/// <summary> Returns the time this user last sent/edited a message, started typing or sent voice data in this server. </summary>
		public DateTime? LastActivityAt { get; private set; }
		/// <summary> Returns the time this user was last seen online in this server. </summary>
		public DateTime? LastOnlineAt => Status != UserStatus.Offline ? DateTime.UtcNow : _lastOnline;
		private DateTime? _lastOnline;

		/// <summary> Returns the private messaging channel with this user, if one exists. </summary>
		[JsonIgnore]
		public Channel PrivateChannel => GlobalUser.PrivateChannel;

		[JsonIgnore]
		internal GlobalUser GlobalUser => _globalUser.Value;
		private readonly Reference<GlobalUser> _globalUser;

		[JsonIgnore]
		public Server Server => _server.Value;
        private readonly Reference<Server> _server;

		[JsonIgnore]
		public Channel VoiceChannel => _voiceChannel.Value;
		private Reference<Channel> _voiceChannel;

		[JsonIgnore]
		public IEnumerable<Role> Roles => _roles.Select(x => x.Value);
		private Dictionary<long, Role> _roles;

		/// <summary> Returns a collection of all messages this user has sent on this server that are still in cache. </summary>
		[JsonIgnore]
		public IEnumerable<Message> Messages
		{
			get
			{
				if (_server.Id != null)
					return Server.Channels.SelectMany(x => x.Messages.Where(y => y.User.Id == Id));
				else
					return GlobalUser.PrivateChannel.Messages.Where(x => x.User.Id == Id);
            }
		}

		/// <summary> Returns a collection of all channels this user is a member of. </summary>
		[JsonIgnore]
		public IEnumerable<Channel> Channels
		{
			get
			{
				if (_server.Id != null)
				{
					return _permissions
						.Where(x => x.Value.Permissions.ReadMessages)
						.Select(x => x.Value.Channel)
						.Where(x => x != null);
				}
				else
				{
					var privateChannel = PrivateChannel;
					if (privateChannel != null)
						return new Channel[] { privateChannel };
					else
						return new Channel[0];
				}
			}
		}

		internal User(DiscordClient client, long id, long? serverId)
			: base(client, id)
		{
			_globalUser = new Reference<GlobalUser>(id, 
				x => _client.GlobalUsers.GetOrAdd(x), 
				x => x.AddUser(this), 
				x => x.RemoveUser(this));
			_server = new Reference<Server>(serverId, 
				x => _client.Servers[x], 
				x =>
				{
					x.AddMember(this);
					if (Id == _client.CurrentUserId)
						x.CurrentUser = this;
                }, 
				x =>
				{
					x.RemoveMember(this);
					if (Id == _client.CurrentUserId)
						x.CurrentUser = null;
				});
			_voiceChannel = new Reference<Channel>(x => _client.Channels[x]);
			_roles = new Dictionary<long, Role>();

			Status = UserStatus.Offline;
			//_channels = new ConcurrentDictionary<string, Channel>();
			if (serverId != null)
			{
				_permissions = new ConcurrentDictionary<long, ChannelPermissionsPair>();
				_serverPermissions = new ServerPermissions();
			}

			if (serverId == null)
				UpdateRoles(null);
		}
		internal override void LoadReferences()
		{
			_globalUser.Load();
			_server.Load();
		}
		internal override void UnloadReferences()
		{
			_globalUser.Unload();
			_server.Unload();
		}

		internal void Update(UserReference model)
		{
			if (model.Avatar != null)
				AvatarId = model.Avatar;
			if (model.Discriminator != null)
				Discriminator = model.Discriminator.Value;
			if (model.Username != null)
				Name = model.Username;
		}
		internal void Update(MemberInfo model)
		{
			if (model.User != null)
				Update(model.User);

			if (model.JoinedAt.HasValue)
				JoinedAt = model.JoinedAt.Value;
			if (model.Roles != null)
				UpdateRoles(model.Roles.Select(x => _client.Roles[x]));

			UpdateServerPermissions();
        }
		internal void Update(ExtendedMemberInfo model)
		{
			Update(model as API.MemberInfo);

			if (model.IsServerDeafened != null)
				IsServerDeafened = model.IsServerDeafened.Value;
			if (model.IsServerMuted != null)
				IsServerMuted = model.IsServerMuted.Value;
		}
		internal void Update(PresenceInfo model)
		{
			if (model.User != null)
				Update(model.User as UserReference);

			if (model.Roles != null)
				UpdateRoles(model.Roles.Select(x => _client.Roles[x]));
			if (model.Status != null && Status != model.Status)
			{
				Status = UserStatus.FromString(model.Status);
				if (Status == UserStatus.Offline)
					_lastOnline = DateTime.UtcNow;
			}
			
			GameId = model.GameId; //Allows null
		}
		internal void Update(VoiceMemberInfo model)
		{
			if (model.IsServerDeafened != null)
				IsServerDeafened = model.IsServerDeafened.Value;
			if (model.IsServerMuted != null)
				IsServerMuted = model.IsServerMuted.Value;
			if (model.SessionId != null)
				SessionId = model.SessionId;
			if (model.Token != null)
				Token = model.Token;
			
			if (model.IsSelfDeafened != null)
				IsSelfDeafened = model.IsSelfDeafened.Value;
			if (model.IsSelfMuted != null)
				IsSelfMuted = model.IsSelfMuted.Value;
			if (model.IsServerSuppressed != null)
				IsServerSuppressed = model.IsServerSuppressed.Value;

			if (_voiceChannel.Id != model.ChannelId)
			{
				var oldChannel = _voiceChannel.Value;
				if (oldChannel != null)
					oldChannel.InvalidateMembersCache();

				_voiceChannel.Id = model.ChannelId; //Can be null

				var newChannel = _voiceChannel.Value;
				if (newChannel != null)
					newChannel.InvalidateMembersCache();
			}
        }
		private void UpdateRoles(IEnumerable<Role> roles)
		{
			Dictionary<long, Role> newRoles = new Dictionary<long, Role>();
			if (roles != null)
			{
				foreach (var r in roles)
					newRoles[r.Id] = r;
			}

			if (_server.Id != null)
			{
				var everyone = Server.EveryoneRole;
				newRoles.Add(everyone.Id, everyone);
			}
			_roles = newRoles;
		}

		internal void UpdateActivity(DateTime? activity = null)
		{
			if (LastActivityAt == null || activity > LastActivityAt.Value)
				LastActivityAt = activity ?? DateTime.UtcNow;
		}

		internal void UpdateServerPermissions()
		{
			var server = Server;
			if (server == null) return;

			uint newPermissions = 0x0;
			uint oldPermissions = _serverPermissions.RawValue;

			if (server.Owner == this)
				newPermissions = ServerPermissions.All.RawValue;
			else
			{
				var roles = Roles;
				foreach (var serverRole in roles)
					newPermissions |= serverRole.Permissions.RawValue;
			}

			if (BitHelper.GetBit(newPermissions, (int)PermissionsBits.ManageRolesOrPermissions))
				newPermissions = ServerPermissions.All.RawValue;

			if (newPermissions != oldPermissions)
			{
				_serverPermissions.SetRawValueInternal(newPermissions);
				foreach (var permission in _permissions)
					UpdateChannelPermissions(permission.Value.Channel);
			}
		}
		internal void UpdateChannelPermissions(Channel channel)
		{
			var server = Server;
			if (server == null) return;
			if (channel.Server != server) throw new InvalidOperationException();

			ChannelPermissionsPair chanPerms;
			if (!_permissions.TryGetValue(channel.Id, out chanPerms)) return;
			uint newPermissions = _serverPermissions.RawValue;
			uint oldPermissions = chanPerms.Permissions.RawValue;
			
			if (server.Owner == this)
				newPermissions = ChannelPermissions.All(channel).RawValue;
			else
			{
				var channelOverwrites = channel.PermissionOverwrites;

				//var roles = Roles.OrderBy(x => x.Id);
				var roles = Roles;
				foreach (var denyRole in channelOverwrites.Where(x => x.TargetType == PermissionTarget.Role && x.Permissions.Deny.RawValue != 0 && roles.Any(y => y.Id == x.TargetId)))
					newPermissions &= ~denyRole.Permissions.Deny.RawValue;
				foreach (var allowRole in channelOverwrites.Where(x => x.TargetType == PermissionTarget.Role && x.Permissions.Allow.RawValue != 0 && roles.Any(y => y.Id == x.TargetId)))
					newPermissions |= allowRole.Permissions.Allow.RawValue;
				foreach (var denyUser in channelOverwrites.Where(x => x.TargetType == PermissionTarget.User && x.TargetId == Id && x.Permissions.Deny.RawValue != 0))
					newPermissions &= ~denyUser.Permissions.Deny.RawValue;
				foreach (var allowUser in channelOverwrites.Where(x => x.TargetType == PermissionTarget.User && x.TargetId == Id && x.Permissions.Allow.RawValue != 0))
					newPermissions |= allowUser.Permissions.Allow.RawValue;
			}

			var mask = ChannelPermissions.All(channel).RawValue;
			if (BitHelper.GetBit(newPermissions, (int)PermissionsBits.ManageRolesOrPermissions))
				newPermissions = mask;
			else if (!BitHelper.GetBit(newPermissions, (int)PermissionsBits.ReadMessages))
				newPermissions = ChannelPermissions.None.RawValue;
			else
				newPermissions &= mask;

			if (newPermissions != oldPermissions)
			{
				chanPerms.Permissions.SetRawValueInternal(newPermissions);
				channel.InvalidateMembersCache();
			}

			chanPerms.Permissions.SetRawValueInternal(newPermissions);
		}

		public ServerPermissions GetServerPermissions() => _serverPermissions;
		public ChannelPermissions GetPermissions(Channel channel)
		{
			if (channel == null) throw new ArgumentNullException(nameof(channel));

			//Return static permissions if this is a private chat
			if (_server.Id == null)
				return ChannelPermissions.PrivateOnly;

			ChannelPermissionsPair chanPerms;
			if (_permissions.TryGetValue(channel.Id, out chanPerms))
				return chanPerms.Permissions;
			return null;
		}

		internal void AddChannel(Channel channel)
		{
			if (_server.Id != null)
			{
				_permissions.TryAdd(channel.Id, new ChannelPermissionsPair(channel));
				UpdateChannelPermissions(channel);
			}
		}
		internal void RemoveChannel(Channel channel)
		{
			if (_server.Id != null)
			{
				ChannelPermissionsPair ignored;
				//_channels.TryRemove(channel.Id, out channel);
				_permissions.TryRemove(channel.Id, out ignored);
			}
		}

		public bool HasRole(Role role)
		{
			if (role == null) throw new ArgumentNullException(nameof(role));
			
			return _roles.ContainsKey(role.Id);
		}

		public override bool Equals(object obj) => obj is User && (obj as User).Id == Id;
		public override int GetHashCode() => unchecked(Id.GetHashCode() + 7230);
		public override string ToString() => Name ?? IdConvert.ToString(Id);
	}
}