﻿using System.Collections.Generic;
using System.Linq;
using DarkRift;
using DarkRift.Server;

namespace ChatPlugin
{
    public class ChatGroup : IDarkRiftSerializable
    {
        public ushort Id { get; }
        public string Name { get; }
        public Dictionary<string, Client> Users;

        public ChatGroup(ushort id, string name)
        {
            Id = id;
            Name = name;
        }

        internal bool AddPlayer(string username, Client client)
        {
            if (Users.ContainsKey(username))
                return false;

            Users[username] = client;
            return true;
        }

        internal void RemovePlayer(string username)
        {
            Users.Remove(username);
        }

        public void Serialize(SerializeEvent e)
        {
            e.Writer.Write(Id);
            e.Writer.Write(Name);
            e.Writer.Write(Users.Keys.ToArray());
        }

        public void Deserialize(DeserializeEvent e)
        {
        }
    }
}