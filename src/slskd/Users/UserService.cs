﻿// <copyright file="UserService.cs" company="slskd Team">
//     Copyright (c) slskd Team. All rights reserved.
//
//     This program is free software: you can redistribute it and/or modify
//     it under the terms of the GNU Affero General Public License as published
//     by the Free Software Foundation, either version 3 of the License, or
//     (at your option) any later version.
//
//     This program is distributed in the hope that it will be useful,
//     but WITHOUT ANY WARRANTY; without even the implied warranty of
//     MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//     GNU Affero General Public License for more details.
//
//     You should have received a copy of the GNU Affero General Public License
//     along with this program.  If not, see https://www.gnu.org/licenses/.
// </copyright>

using Microsoft.Extensions.Options;

namespace slskd.Users
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Threading.Tasks;
    using Microsoft.EntityFrameworkCore;
    using Serilog;
    using Soulseek;

    /// <summary>
    ///     Provides information and operations for network peers.
    /// </summary>
    public class UserService : IUserService
    {
        private const int CacheTTLSeconds = 300;

        /// <summary>
        ///     Initializes a new instance of the <see cref="UserService"/> class.
        /// </summary>
        /// <param name="soulseekClient"></param>
        /// <param name="stateMutator"></param>
        /// <param name="contextFactory">The database context to use.</param>
        /// <param name="optionsMonitor"></param>
        public UserService(
            ISoulseekClient soulseekClient,
            IStateMutator<State> stateMutator,
            IDbContextFactory<UserDbContext> contextFactory,
            IOptionsMonitor<Options> optionsMonitor)
        {
            Client = soulseekClient;

            StateMutator = stateMutator;

            ContextFactory = contextFactory;

            OptionsMonitor = optionsMonitor;
            OptionsMonitor.OnChange(Configure);

            BindClientEvents();
            Configure(OptionsMonitor.CurrentValue);
        }

        /// <summary>
        ///     Gets the list of tracked users.
        /// </summary>
        public IReadOnlyList<User> Users => UserDictionary.Values.ToList().AsReadOnly();

        /// <summary>
        ///     Gets the list of watched usernames.
        /// </summary>
        public IReadOnlyList<string> WatchedUsernames => WatchedUsernamesDictionary.Keys.ToList().AsReadOnly();

        private ISoulseekClient Client { get; }
        private IDbContextFactory<UserDbContext> ContextFactory { get; }
        private string LastOptionsHash { get; set; }
        private ILogger Log { get; set; } = Serilog.Log.ForContext<UserService>();
        private IOptionsMonitor<Options> OptionsMonitor { get; }
        private IStateMutator<State> StateMutator { get; }
        private ConcurrentDictionary<string, User> UserDictionary { get; set; } = new ConcurrentDictionary<string, User>();
        private ConcurrentDictionary<string, bool> WatchedUsernamesDictionary { get; set; } = new ConcurrentDictionary<string, bool>();

        /// <summary>
        ///     Gets the name of the group for the specified <paramref name="username"/>.
        /// </summary>
        /// <param name="username">The username of the peer.</param>
        /// <returns>The group for the specified username.</returns>
        public string GetGroup(string username)
        {
            // note: this is an extremely hot path; keep the work done to an absolute minimum.
            if (UserDictionary.TryGetValue(username ?? string.Empty, out var user))
            {
                if (user.Status?.IsPrivileged ?? false)
                {
                    return Application.PrivilegedGroup;
                }

                return user.Group ?? Application.DefaultGroup;
            }

            return Application.DefaultGroup;
        }

        /// <summary>
        ///     Retrieves peer <see cref="Info"/>.
        /// </summary>
        /// <param name="username">The username of the peer.</param>
        /// <param name="bypassCache">A value indicating whether the local cache should be bypassed.</param>
        /// <returns>The cached or retrieved info.</returns>
        public async Task<Info> GetInfoAsync(string username, bool bypassCache = false)
        {
            using var context = ContextFactory.CreateDbContext();

            var info = await context.Info.AsNoTracking().FirstOrDefaultAsync(peer => peer.Username == username);
            var peerExists = info != default;

            if (!bypassCache && peerExists && info.UpdatedAt < DateTime.UtcNow.AddSeconds(CacheTTLSeconds))
            {
                return info;
            }

            var soulseekUserInfo = await Client.GetUserInfoAsync(username);
            info = Info.FromSoulseekUserInfo(username, soulseekUserInfo);

            if (peerExists)
            {
                context.Update(info);
            }
            else
            {
                context.Add(info);
            }

            await context.SaveChangesAsync();

            return info;
        }

        /// <summary>
        ///     Retrieves a peer's IP endpoint, including their IP address and listen port.
        /// </summary>
        /// <param name="username">The username of the peer.</param>
        /// <returns>The retrieved endpoint.</returns>
        public Task<IPEndPoint> GetIPEndPointAsync(string username)
        {
            return Client.GetUserEndPointAsync(username);
        }

        /// <summary>
        ///     Retrieves the current <see cref="Statistics"/> of a peer.
        /// </summary>
        /// <param name="username">The username of the peer.</param>
        /// <returns>The retrieved statistics.</returns>
        public async Task<Statistics> GetStatisticsAsync(string username)
        {
            var soulseekStatistics = await Client.GetUserStatisticsAsync(username);
            return Statistics.FromSoulseekUserStatistics(soulseekStatistics);
        }

        /// <summary>
        ///     Retrieves the current <see cref="Status"/> of a peer.
        /// </summary>
        /// <param name="username">The username of the peer.</param>
        /// <returns>The retrieved status.</returns>
        public async Task<Status> GetStatusAsync(string username)
        {
            var soulseekStatus = await Client.GetUserStatusAsync(username);
            return Status.FromSoulseekUserStatus(soulseekStatus);
        }

        /// <summary>
        ///     Grants the specified peer the specified number of privilege days.
        /// </summary>
        /// <param name="username">The username of the peer.</param>
        /// <param name="days">The number of days to grant.</param>
        /// <returns>The operation context.</returns>
        public Task GrantPrivilegesAsync(string username, int days)
            => Client.GrantUserPrivilegesAsync(username, days);

        /// <summary>
        ///     Retrieves a value indicating whether the specified peer is privileged.
        /// </summary>
        /// <param name="username">The username of the peer.</param>
        /// <returns>A value indicating whether the specified peer is privileged.</returns>
        public async Task<bool> IsPrivilegedAsync(string username)
        {
            var status = await GetStatusAsync(username);
            return status.IsPrivileged;
        }

        /// <summary>
        ///     Gets a value indicating whether the specified <paramref name="username"/> is watched.
        /// </summary>
        /// <param name="username">The username of the peer.</param>
        /// <returns>A value indicating whether the username is watched.</returns>
        public bool IsWatched(string username) => WatchedUsernamesDictionary.ContainsKey(username);

        /// <summary>
        ///     Adds the specified username to the server-side user list.
        /// </summary>
        /// <param name="username">The username of the peer.</param>
        /// <returns>The operation context.</returns>
        public async Task WatchAsync(string username)
        {
            // note that this isn't strictly synchronized, and this may be executed more than once per user. this is acceptable,
            // and has no side effects other than increased round trips to the server.
            if (!WatchedUsernamesDictionary.ContainsKey(username))
            {
                await Client.AddUserAsync(username);
                WatchedUsernamesDictionary.TryAdd(username, true);

                Log.Information("Added user {Username} to watch list", username);

                // the server does not send a status update when a user is added, but does when that user's status changes.
                // request the status explicitly to get the current status.
                await GetStatusAsync(username);
            }
        }

        private void BindClientEvents()
        {
            // it isn't completely clear what all causes the server to send this message. we know it is sent following an explicit
            // request, but it may be sent in an unsolicited manner as well. to be safe, we update internal stats for this user
            // only on this event, which should cover all bases (both solicited and unsolicited).
            Client.UserStatisticsChanged += (_, e) =>
            {
                var statistics = Statistics.FromSoulseekUserStatistics(e);

                UserDictionary.AddOrUpdate(
                    key: e.Username,
                    addValue: new User() { Statistics = statistics },
                    updateValueFactory: (key, user) => user with { Username = e.Username, Statistics = statistics });

                StateMutator.SetValue(state => state with { Users = Users.ToArray() });
            };

            // the server sends this message in both solicited (we request status), and unsolicited (the user's status changes and
            // the server informs us without asking). we handle updates in this event handler to cover both cases.
            Client.UserStatusChanged += (_, e) =>
            {
                var status = Status.FromSoulseekUserStatus(e);

                UserDictionary.AddOrUpdate(
                    key: e.Username,
                    addValue: new User() { Status = status },
                    updateValueFactory: (key, user) => user with { Username = e.Username, Status = status });

                StateMutator.SetValue(state => state with { Users = Users.ToArray() });

                // the server doesn't send statistics events by itself, so when a user status changes, fetch stats at the same time.
                _ = GetStatisticsAsync(e.Username);
            };

            Client.LoggedIn += (_, _) => WatchAllUsers();
            Client.Disconnected += (_, _) => WatchedUsernamesDictionary.Clear();
        }

        private void Configure(Options options)
        {
            var optionsHash = Compute.Sha1Hash(options.Groups.UserDefined.ToJson());

            if (optionsHash == LastOptionsHash)
            {
                return;
            }

            var usernamesBeforeUpdate = UserDictionary.Keys.ToList();
            var usernamesAfterUpdate = options.Groups.UserDefined.SelectMany(g => g.Value.Members);
            var usernamesRemoved = usernamesBeforeUpdate.Except(usernamesAfterUpdate);

            foreach (var username in usernamesRemoved)
            {
                UserDictionary.AddOrUpdate(
                    key: username,
                    addValue: new User() { Username = username },
                    updateValueFactory: (key, user) => user with { Username = username, Group = null });
            }

            // sort by priority, descending. this will cause the highest priority group for the user to be persisted when the
            // operation is complete.
            foreach (var group in options.Groups.UserDefined.OrderByDescending(kvp => kvp.Value.Upload.Priority))
            {
                foreach (var username in group.Value.Members)
                {
                    UserDictionary.AddOrUpdate(
                        key: username,
                        addValue: new User() { Username = username, Group = group.Key },
                        updateValueFactory: (key, user) => user with { Username = username, Group = group.Key });
                }
            }

            StateMutator.SetValue(state => state with { Users = Users.ToArray() });

            WatchAllUsers();

            LastOptionsHash = optionsHash;
        }

        private void WatchAllUsers()
        {
            if (Client.State.HasFlag(SoulseekClientStates.Connected) && Client.State.HasFlag(SoulseekClientStates.LoggedIn))
            {
                foreach (var username in UserDictionary.Keys.ToList())
                {
                    _ = WatchAsync(username);
                }
            }
        }
    }
}