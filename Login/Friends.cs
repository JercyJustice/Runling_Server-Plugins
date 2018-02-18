﻿using DarkRift;
using DarkRift.Server;
using DbConnectorPlugin;
using MongoDB.Driver;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace LoginPlugin
{
    public class Friends : Plugin
    {
        public override Version Version => new Version(1, 0, 0);
        public override bool ThreadSafe => false;

        public override Command[] Commands => new[]
        {
            new Command("AddFriend", "Adds a User to the Database [AddFriend name friend]", "", AddFriendCommand),
            new Command("DelFriend", "Deletes a User from the Database [DelFriend name friend]", "", DelFriendCommand)
        };

        // Tag
        private const byte FriendsTag = 1;
        private const ushort Shift = FriendsTag * Login.TagsPerPlugin;

        // Subjects
        private const ushort FriendRequest = 0 + Shift;
        private const ushort RequestFailed = 1 + Shift;
        private const ushort RequestSuccess = 2 + Shift;
        private const ushort AcceptRequest = 3 + Shift;
        private const ushort AcceptRequestSuccess = 4 + Shift;
        private const ushort AcceptRequestFailed = 5 + Shift;
        private const ushort DeclineRequest = 6 + Shift;
        private const ushort DeclineRequestSuccess = 7 + Shift;
        private const ushort DeclineRequestFailed = 8 + Shift;
        private const ushort RemoveFriend = 9 + Shift;
        private const ushort RemoveFriendSuccess = 10 + Shift;
        private const ushort RemoveFriendFailed = 11 + Shift;
        private const ushort GetAllFriends = 12 + Shift;
        private const ushort GetAllFriendsFailed = 13 + Shift;
        private const ushort FriendLoggedIn = 14 + Shift;
        private const ushort FriendLoggedOut = 15 + Shift;

        private const string ConfigPath = @"Plugins\Friends.xml";
        private DbConnector _dbConnector;
        private Login _loginPlugin;
        private bool _debug = true;

        public Friends(PluginLoadData pluginLoadData) : base(pluginLoadData)
        {
            LoadConfig();
            ClientManager.ClientConnected += OnPlayerConnected;
        }

        private void LoadConfig()
        {
            XDocument document;

            if (!File.Exists(ConfigPath))
            {
                document = new XDocument(new XDeclaration("1.0", "utf-8", "yes"),
                    new XComment("Settings for the Friends Plugin"),
                    new XElement("Variables", new XAttribute("Debug", true))
                );
                try
                {
                    document.Save(ConfigPath);
                    WriteEvent("Created /Plugins/Friends.xml!", LogType.Warning);
                }
                catch (Exception ex)
                {
                    WriteEvent("Failed to create Friends.xml: " + ex.Message + " - " + ex.StackTrace, LogType.Error);
                }
            }
            else
            {
                try
                {
                    document = XDocument.Load(ConfigPath);
                    _debug = document.Element("Variables").Attribute("Debug").Value == "true";
                }
                catch (Exception ex)
                {
                    WriteEvent("Failed to load Friends.xml: " + ex.Message + " - " + ex.StackTrace, LogType.Error);
                }
            }
        }

        private void OnPlayerConnected(object sender, ClientConnectedEventArgs e)
        {
            // If you have DR2 Pro, use the Plugin.Loaded() method to get the DbConnector Plugin instead
            if (_dbConnector == null)
            {
                _dbConnector = PluginManager.GetPluginByType<DbConnector>();
                _loginPlugin = PluginManager.GetPluginByType<Login>();

                _loginPlugin.onLogout += LogoutFriend;
            }

            e.Client.MessageReceived += OnMessageReceived;
        }

        private void OnMessageReceived(object sender, MessageReceivedEventArgs e)
        {
            using (var message = e.GetMessage())
            {
                // Check if message is meant for this plugin
                if (message.Tag < Login.TagsPerPlugin * FriendsTag || message.Tag >= Login.TagsPerPlugin * (FriendsTag + 1))
                    return;

                var client = e.Client;

                switch (message.Tag)
                {
                    case FriendRequest:
                    {
                        // If player isn't logged in -> return error 1
                        if (!_loginPlugin.PlayerLoggedIn(client, RequestFailed, "Friend request failed."))
                            return;

                        var senderName = _loginPlugin.Users[client];
                        string receiver;

                        try
                        {
                            using (var reader = message.GetReader())
                            {
                                receiver = reader.ReadString();
                            }
                        }
                        catch (Exception ex)
                        {
                            // Return Error 0 for Invalid Data Packages Recieved
                            _loginPlugin.InvalidData(client, FriendsTag, RequestFailed, ex, "Friend Request Failed! ");
                            return;
                        }

                        try
                        {
                            var receiverFriendList = _dbConnector.FriendLists.AsQueryable()
                                .FirstOrDefault(u => u.Username == receiver);
                            if (receiverFriendList == null)
                            {
                                // No user with that name found -> return error 3
                                using (var writer = DarkRiftWriter.Create())
                                {
                                    writer.Write((byte) 3);

                                    using (var msg = Message.Create(RequestFailed, writer))
                                    {
                                        client.SendMessage(msg, SendMode.Reliable);
                                    }
                                }

                                if (_debug)
                                {
                                    WriteEvent("No user named " + receiver + " found!", LogType.Info);
                                }
                                return;
                            }

                            if (receiverFriendList.Friends.Contains(senderName) ||
                                receiverFriendList.OpenFriendRequests.Contains(senderName))
                            {
                                // Users are already friends or have an open request -> return error 4
                                using (var writer = DarkRiftWriter.Create())
                                {
                                    writer.Write((byte) 4);

                                    using (var msg = Message.Create(RequestFailed, writer))
                                    {
                                        client.SendMessage(msg, SendMode.Reliable);
                                    }
                                }

                                if (_debug)
                                {
                                    WriteEvent("Request failed, " + senderName + " and " + receiver +
                                               " were already friends or had an open friend request!", LogType.Info);
                                }
                                return;
                            }

                            // Save the request in the database to both users
                            AddRequests(senderName, receiver);

                            using (var writer = DarkRiftWriter.Create())
                            {
                                writer.Write(receiver);

                                using (var msg = Message.Create(RequestSuccess, writer))
                                {
                                    client.SendMessage(msg, SendMode.Reliable);
                                }
                            }

                            if (_debug)
                            {
                                WriteEvent(senderName + " wants to add " + receiver + " as a friend!", LogType.Info);
                            }

                            // If Receiver is currently logged in, let him know right away
                            if (_loginPlugin.Clients.ContainsKey(receiver))
                            {
                                var receivingClient = _loginPlugin.Clients[receiver];

                                using (var writer = DarkRiftWriter.Create())
                                {
                                    writer.Write(senderName);

                                    using (var msg = Message.Create(FriendRequest, writer))
                                    {
                                        receivingClient.SendMessage(msg, SendMode.Reliable);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            // Return Error 2 for Database error
                            _dbConnector.DatabaseError(client, RequestFailed, ex);
                        }
                        break;
                    }
                    case DeclineRequest:
                    {
                        // If player isn't logged in -> return error 1
                        if (!_loginPlugin.PlayerLoggedIn(client, DeclineRequestFailed, "DeclineFriendRequest failed."))
                            return;

                        var senderName = _loginPlugin.Users[client];
                        string receiver;

                        try
                        {
                            using (var reader = message.GetReader())
                            {
                                receiver = reader.ReadString();
                            }
                        }
                        catch (Exception ex)
                        {
                            // Return Error 0 for Invalid Data Packages Recieved
                            _loginPlugin.InvalidData(client, FriendsTag, DeclineRequestFailed, ex, "Decline Request Failed!");
                            return;
                        }

                        try
                        {
                            // Delete the request from the database for both users
                            RemoveRequests(senderName, receiver);

                            using (var writer = DarkRiftWriter.Create())
                            {
                                writer.Write(receiver);
                                writer.Write(true);

                                using (var msg = Message.Create(DeclineRequestSuccess, writer))
                                {
                                    client.SendMessage(msg, SendMode.Reliable);
                                }
                            }

                            if (_debug)
                            {
                                WriteEvent(senderName + " declined " + receiver + "'s friend request.", LogType.Info);
                            }

                            // If Receiver is currently logged in, let him know right away
                            if (_loginPlugin.Clients.ContainsKey(receiver))
                            {
                                var receivingClient = _loginPlugin.Clients[receiver];

                                using (var writer = DarkRiftWriter.Create())
                                {
                                    writer.Write(senderName);
                                    writer.Write(false);

                                    using (var msg = Message.Create(DeclineRequestSuccess, writer))
                                    {
                                        receivingClient.SendMessage(msg, SendMode.Reliable);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            // Return Error 2 for Database error
                            _dbConnector.DatabaseError(client, DeclineRequestFailed, ex);
                        }
                        break;
                    }
                    case AcceptRequest:
                    {
                        // If player isn't logged in -> return error 1
                        if (!_loginPlugin.PlayerLoggedIn(client, AcceptRequestFailed, "AcceptFriendRequest failed."))
                            return;

                        var senderName = _loginPlugin.Users[client];
                        string receiver;

                        try
                        {
                            using (var reader = message.GetReader())
                            {
                                receiver = reader.ReadString();
                            }
                        }
                        catch (Exception ex)
                        {
                            // Return Error 0 for Invalid Data Packages Recieved
                            _loginPlugin.InvalidData(client, FriendsTag, AcceptRequestFailed, ex, "Accept Request Failed!");
                            return;
                        }

                        try
                        {
                            // Delete the request from the database for both users and add their names to their friend list
                            RemoveRequests(senderName, receiver);
                            AddFriends(senderName, receiver);

                            var receiverOnline = _loginPlugin.Clients.ContainsKey(receiver);

                            using (var writer = DarkRiftWriter.Create())
                            {
                                writer.Write(receiver);
                                writer.Write(receiverOnline);

                                using (var msg = Message.Create(AcceptRequestSuccess, writer))
                                {
                                    client.SendMessage(msg, SendMode.Reliable);
                                }
                            }

                            if (_debug)
                            {
                                WriteEvent(senderName + " accepted " + receiver + "'s friend request.", LogType.Info);
                            }

                            // If Receiver is currently logged in, let him know right away
                            if (receiverOnline)
                            {
                                var receivingClient = _loginPlugin.Clients[receiver];

                                using (var writer = DarkRiftWriter.Create())
                                {
                                    writer.Write(senderName);
                                    writer.Write(true);

                                    using (var msg = Message.Create(AcceptRequestSuccess, writer))
                                    {
                                        receivingClient.SendMessage(msg, SendMode.Reliable);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            // Return Error 2 for Database error
                            _dbConnector.DatabaseError(client, AcceptRequestFailed, ex);
                        }
                        break;
                    }
                    case RemoveFriend:
                    {
                        // If player isn't logged in -> return error 1
                        if (!_loginPlugin.PlayerLoggedIn(client, RemoveFriendFailed, "RemoveFriend failed."))
                            return;
                
                        var senderName = _loginPlugin.Users[client];
                        string receiver;

                        try
                        {
                            using (var reader = message.GetReader())
                            {
                                receiver = reader.ReadString();
                            }
                        }
                        catch (Exception ex)
                        {
                            // Return Error 0 for Invalid Data Packages Recieved
                            _loginPlugin.InvalidData(client, FriendsTag, RemoveFriendFailed, ex, "Remove Friend Failed!");
                            return;
                        }

                        try
                        {
                            // Delete the names from the friendlist in the database for both users
                            RemoveFriends(senderName, receiver);

                            using (var writer = DarkRiftWriter.Create())
                            {
                                writer.Write(receiver);
                                writer.Write(true);

                                using (var msg = Message.Create(RemoveFriendSuccess, writer))
                                {
                                    client.SendMessage(msg, SendMode.Reliable);
                                }
                            }

                            if (_debug)
                            {
                                WriteEvent(senderName + " removed " + receiver + " as a friend.", LogType.Info);
                            }

                            // If Receiver is currently logged in, let him know right away
                            if (_loginPlugin.Clients.ContainsKey(receiver))
                            {
                                var receivingClient = _loginPlugin.Clients[receiver];

                                using (var writer = DarkRiftWriter.Create())
                                {
                                    writer.Write(senderName);
                                    writer.Write(false);

                                    using (var msg = Message.Create(RemoveFriendSuccess, writer))
                                    {
                                        receivingClient.SendMessage(msg, SendMode.Reliable);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            // Return Error 2 for Database error
                            _dbConnector.DatabaseError(client, RemoveFriendFailed, ex);
                        }
                        break;
                    }
                    case GetAllFriends:
                    {
                        // If player isn't logged in -> return error 1
                        if (!_loginPlugin.PlayerLoggedIn(client, GetAllFriendsFailed, "GetAllFriends failed."))
                            return;

                        var senderName = _loginPlugin.Users[client];

                        try
                        {
                            var friendList = _dbConnector.FriendLists.AsQueryable().First(fL => fL.Username == senderName);
                            var onlineFriends = new List<string>();
                            var offlineFriends = new List<string>();

                            using (var writer = DarkRiftWriter.Create())
                            {
                                writer.Write(senderName);

                                // let online friends know he logged in
                                foreach (var friend in friendList.Friends)
                                {
                                    if (_loginPlugin.Clients.ContainsKey(friend))
                                    {
                                        onlineFriends.Add(friend);

                                        using (var msg = Message.Create(FriendLoggedIn, writer))
                                        {
                                            _loginPlugin.Clients[friend].SendMessage(msg, SendMode.Reliable);
                                        }
                                    }
                                    else
                                    {
                                        offlineFriends.Add(friend);
                                    }
                                }
                            }

                            using (var writer = DarkRiftWriter.Create())
                            {
                                writer.Write(onlineFriends.ToArray());
                                writer.Write(offlineFriends.ToArray());
                                writer.Write(friendList.OpenFriendRequests.ToArray());
                                writer.Write(friendList.UnansweredFriendRequests.ToArray());

                                using (var msg = Message.Create(GetAllFriends, writer))
                                {
                                    client.SendMessage(msg, SendMode.Reliable);
                                }
                            }

                            if (_debug)
                            {
                                WriteEvent("Got friends for " + senderName, LogType.Info);
                            }
                        }
                        catch (Exception ex)
                        {
                            // Return Error 2 for Database error
                            _dbConnector.DatabaseError(client, GetAllFriendsFailed, ex);
                        }
                        break;
                    }
                }
            }
        }

        public void LogoutFriend(string username)
        {
            var friends = _dbConnector.FriendLists.AsQueryable().First(fL => fL.Username == username).Friends;
            using (var writer = DarkRiftWriter.Create())
            {
                writer.Write(username);

                using (var msg = Message.Create(FriendLoggedOut, writer))
                {
                    // let online friends know he logged out
                    foreach (var friend in friends)
                    {
                        if (_loginPlugin.Clients.ContainsKey(friend))
                        {
                            _loginPlugin.Clients[friend].SendMessage(msg, SendMode.Reliable);
                        }
                    }
                }
            }
        }

        #region DbHelpers

        private void AddRequests(string sender, string receiver)
        {
            var updateReceiving = Builders<FriendList>.Update.AddToSet(fL => fL.OpenFriendRequests, sender);
            _dbConnector.FriendLists.UpdateOne(fL => fL.Username == receiver, updateReceiving);
            var updateSender = Builders<FriendList>.Update.AddToSet(fL => fL.UnansweredFriendRequests, receiver);
            _dbConnector.FriendLists.UpdateOne(u => u.Username == sender, updateSender);
        }

        private void RemoveRequests(string sender, string receiver)
        {
            var updateSender = Builders<FriendList>.Update.Pull(fL => fL.OpenFriendRequests, receiver);
            _dbConnector.FriendLists.UpdateOne(u => u.Username == sender, updateSender);
            var updateReceiving = Builders<FriendList>.Update.Pull(fL => fL.UnansweredFriendRequests, sender);
            _dbConnector.FriendLists.UpdateOne(u => u.Username == receiver, updateReceiving);
        }

        private void AddFriends(string sender, string receiver)
        {
            var updateReceiving = Builders<FriendList>.Update.AddToSet(fL => fL.Friends, sender);
            _dbConnector.FriendLists.UpdateOne(u => u.Username == receiver, updateReceiving);
            var updateSending = Builders<FriendList>.Update.AddToSet(fL => fL.Friends, receiver);
            _dbConnector.FriendLists.UpdateOne(u => u.Username == sender, updateSending);
        }

        private void RemoveFriends(string sender, string receiver)
        {
            var senderFriendList = _dbConnector.FriendLists.AsQueryable().First(fL => fL.Username == sender);
            var receiverFriendList = _dbConnector.FriendLists.AsQueryable().First(fL => fL.Username == receiver);

            // Update sender
            if (senderFriendList.Friends.Contains(receiver))
            {
                var updateSender = Builders<FriendList>.Update.Pull(fL => fL.Friends, receiver);
                _dbConnector.FriendLists.UpdateOne(fL => fL.Username == sender, updateSender);
            }
            if (senderFriendList.OpenFriendRequests.Contains(receiver))
            {
                var updateSender = Builders<FriendList>.Update.Pull(fL => fL.OpenFriendRequests, receiver);
                _dbConnector.FriendLists.UpdateOne(fL => fL.Username == sender, updateSender);
            }
            if (senderFriendList.UnansweredFriendRequests.Contains(receiver))
            {
                var updateSender = Builders<FriendList>.Update.Pull(fL => fL.UnansweredFriendRequests, receiver);
                _dbConnector.FriendLists.UpdateOne(fL => fL.Username == sender, updateSender);
            }

            //Update receiver
            if (receiverFriendList.Friends.Contains(sender))
            {
                var updateReceiver = Builders<FriendList>.Update.Pull(fL => fL.Friends, sender);
                _dbConnector.FriendLists.UpdateOne(fL => fL.Username == receiver, updateReceiver);
            }
            if (receiverFriendList.OpenFriendRequests.Contains(sender))
            {
                var updateReceiver = Builders<FriendList>.Update.Pull(fL => fL.OpenFriendRequests, sender);
                _dbConnector.FriendLists.UpdateOne(fL => fL.Username == receiver, updateReceiver);
            }
            if (receiverFriendList.UnansweredFriendRequests.Contains(sender))
            {
                var updateReceiver = Builders<FriendList>.Update.Pull(fL => fL.UnansweredFriendRequests, sender);
                _dbConnector.FriendLists.UpdateOne(fL => fL.Username == receiver, updateReceiver);
            }
        }

        #endregion

        #region Commands

        private void AddFriendCommand(object sender, CommandEventArgs e)
        {
            if (_dbConnector == null)
            {
                _dbConnector = PluginManager.GetPluginByType<DbConnector>();
            }

            if (e.Arguments.Length != 2)
            {
                WriteEvent("Invalid arguments. Enter [AddFríend name friend].", LogType.Warning);
                return;
            }

            var username = e.Arguments[0];
            var friend = e.Arguments[1];

            try
            {
                AddFriends(username, friend);

                if (_debug)
                {
                    WriteEvent("Added " + friend + " as a friend of " + username, LogType.Info);
                }
            }
            catch (Exception ex)
            {
                WriteEvent("Database Error: " + ex.Message + " - " + ex.StackTrace, LogType.Error);
            }
        }

        private void DelFriendCommand(object sender, CommandEventArgs e)
        {
            if (_dbConnector == null)
            {
                _dbConnector = PluginManager.GetPluginByType<DbConnector>();
            }

            if (e.Arguments.Length != 2)
            {
                WriteEvent("Invalid arguments. Enter [AddFríend name friend].", LogType.Warning);
                return;
            }

            var username = e.Arguments[0];
            var friend = e.Arguments[1];

            try
            {
                RemoveFriends(username, friend);

                if (_debug)
                {
                    WriteEvent("Removed " + friend + " as a friend of " + username, LogType.Info);
                }
            }
            catch (Exception ex)
            {
                WriteEvent("Database Error: " + ex.Message + " - " + ex.StackTrace, LogType.Error);
            }
        }
        #endregion
    }
}
