﻿/*
This file is part of Depressurizer.
Copyright 2011, 2012, 2013 Steve Labbe.

Depressurizer is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

Depressurizer is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with Depressurizer.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.IO;

namespace Depressurizer
{
    [Flags]
    public enum AppPlatforms
    {
        None = 0,
        Windows = 1,
        Mac = 1 << 1,
        Linux = 1 << 2,
        All = Windows | Mac | Linux
    }

    [Flags]
    public enum AppTypes
    {
        Application = 1,
        Demo = 1 << 1,
        DLC = 1 << 2,
        Game = 1 << 3,
        Media = 1 << 4,
        Tool = 1 << 5,
        Other = 1 << 6,
        Unknown = 1 << 7,
        InclusionNormal = Application | Game,
        InclusionUnknown = InclusionNormal | Unknown,
        InclusionAll = (1 << 8) - 1
    }

    internal class AppInfo
    {
        public AppTypes AppType;
        public int Id;
        public string Name;
        public int Parent; // 0 if none
        public AppPlatforms Platforms;

        public AppInfo(int id, string name = null, AppTypes type = AppTypes.Unknown,
            AppPlatforms platforms = AppPlatforms.All)
        {
            Id = id;
            Name = name;
            AppType = type;

            Platforms = platforms;
        }

        public static AppInfo FromVdfNode(VdfFileNode commonNode)
        {
            if (commonNode == null || commonNode.NodeType != ValueType.Array) return null;

            AppInfo result = null;

            var idNode = commonNode.GetNodeAt(new[] {"gameid"}, false);
            var id = -1;
            if (idNode != null)
            {
                if (idNode.NodeType == ValueType.Int)
                    id = idNode.NodeInt;
                else if (idNode.NodeType == ValueType.String)
                    if (!int.TryParse(idNode.NodeString, out id))
                        id = -1;
            }

            if (id >= 0)
            {
                // Get name
                string name = null;
                var nameNode = commonNode.GetNodeAt(new[] {"name"}, false);
                if (nameNode != null) name = nameNode.NodeData.ToString();

                // Get type
                string typeStr = null;
                var type = AppTypes.Unknown;
                var typeNode = commonNode.GetNodeAt(new[] {"type"}, false);
                if (typeNode != null) typeStr = typeNode.NodeData.ToString();

                if (typeStr != null)
                    if (!Enum.TryParse(typeStr, true, out type))
                        type = AppTypes.Other;

                // Get platforms
                string oslist = null;
                var platforms = AppPlatforms.None;
                var oslistNode = commonNode.GetNodeAt(new[] {"oslist"}, false);
                if (oslistNode != null)
                {
                    oslist = oslistNode.NodeData.ToString();
                    if (oslist.IndexOf("windows", StringComparison.OrdinalIgnoreCase) != -1)
                        platforms |= AppPlatforms.Windows;
                    if (oslist.IndexOf("mac", StringComparison.OrdinalIgnoreCase) != -1) platforms |= AppPlatforms.Mac;
                    if (oslist.IndexOf("linux", StringComparison.OrdinalIgnoreCase) != -1)
                        platforms |= AppPlatforms.Linux;
                }

                result = new AppInfo(id, name, type, platforms);

                // Get parent
                var parentNode = commonNode.GetNodeAt(new[] {"parent"}, false);
                if (parentNode != null) result.Parent = parentNode.NodeInt;
            }

            return result;
        }

        public static Dictionary<int, AppInfo> LoadApps(string path)
        {
            var result = new Dictionary<int, AppInfo>();
            var bReader = new BinaryReader(new FileStream(path, FileMode.Open, FileAccess.Read));
            var fileLength = bReader.BaseStream.Length;

            // seek to common: start of a new entry
            byte[] start = {0x00, 0x00, 0x63, 0x6F, 0x6D, 0x6D, 0x6F, 0x6E, 0x00}; // 0x00 0x00 c o m m o n 0x00

            VdfFileNode.ReadBin_SeekTo(bReader, start, fileLength);

            var node = VdfFileNode.LoadFromBinary(bReader, fileLength);
            while (node != null)
            {
                var app = FromVdfNode(node);
                if (app != null) result.Add(app.Id, app);
                VdfFileNode.ReadBin_SeekTo(bReader, start, fileLength);
                node = VdfFileNode.LoadFromBinary(bReader, fileLength);
            }

            bReader.Close();
            return result;
        }
    }
}