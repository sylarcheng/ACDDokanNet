﻿using Azi.Amazon.CloudDrive;
using Azi.Amazon.CloudDrive.JsonObjects;
using Azi.Tools;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Diagnostics;

namespace Azi.ACDDokanNet
{


    public class SmallFileCache
    {
        static ConcurrentDictionary<AmazonDrive, SmallFileCache> Instances = new ConcurrentDictionary<AmazonDrive, SmallFileCache>(10, 3);
        public static SmallFileCache GetInstance(AmazonDrive amazon) => Instances.GetOrAdd(amazon, (a) => new SmallFileCache(a));

        readonly AmazonDrive amazon;
        public readonly static string CachePath = Path.Combine(Path.GetTempPath(), "CloudDriveTestCache");

        public void StartDownload(FSItem node, string path)
        {
            try
            {
                var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (writer.Length == node.Length)
                {
                    writer.Close();
                    return;
                }
                Task.Run(async () => await Download(node, writer));
            }
            catch (IOException e)
            {
                Log.Warn("File is already downloading: " + node.Id + "\r\n" + e);
            }
        }

        private async Task Download(FSItem node, Stream writer)
        {
            Log.Trace("Started download: " + node.Id);
            var start = Stopwatch.StartNew();
            var buf = new byte[4096];
            using (writer)
                try
                {
                    while (writer.Length < node.Length)
                    {
                        await amazon.Files.Download(node.Id, fileOffset: writer.Length, streammer: async (stream) =>
                        {
                            int red = 0;
                            do
                            {
                                red = await stream.ReadAsync(buf, 0, buf.Length);
                                if (writer.Length == 0) Log.Trace("Got first part: " + node.Id + " in " + start.ElapsedMilliseconds);
                                writer.Write(buf, 0, red);
                            } while (red > 0);
                        });
                        if (writer.Length < node.Length) await Task.Delay(500);
                    }
                    Log.Trace("Finished download: " + node.Id);
                    writer.Close();
                }
                catch (Exception ex)
                {
                    Log.Error($"Download failed: {node.Id}\r\n{ex}");
                }
        }

        private SmallFileCache(AmazonDrive a)
        {
            amazon = a;
            Directory.CreateDirectory(CachePath);
        }

        public IBlockStream OpenRead(FSItem node)
        {
            var path = Path.Combine(CachePath, node.Id);
            StartDownload(node, path);

            Log.Trace("Opened cached: " + node.Id);
            return new FileBlockReader(path);
        }
    }
}