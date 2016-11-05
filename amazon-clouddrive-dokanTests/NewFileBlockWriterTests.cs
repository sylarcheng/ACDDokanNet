﻿using Azi.Cloud.Common;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Azi.Cloud.DokanNet.Tests
{
    public sealed class NewFileBlockWriterTests : IDisposable
    {
        private NewFileBlockWriter writer;

        private FSItem item = new FSItem.Builder()
        {
            Id = "id",
            ParentPath = "p",
            Name = "n",
            ParentIds = new System.Collections.Concurrent.ConcurrentBag<string>(new string[] { "id" })
        }.Build();

        private string tempPath = Path.Combine(Path.GetTempPath(), Path.GetTempFileName());

        public NewFileBlockWriterTests()
        {
            writer = new NewFileBlockWriter(item, tempPath);
        }

        [Fact]
        public void MultipleParallelWriteTest()
        {
            for (var i = 0; i < 10; i++)
            {
                ParallelWriteTest();
            }
        }

        public void ParallelWriteTest()
        {
            writer = new NewFileBlockWriter(item, tempPath);

            var array = Enumerable.Range(0, 1 << 10).Select(i => (byte)(i & 255)).ToArray();
            const int parts = 8;
            var tasks = new Task[parts];
            for (var i = 0; i < parts; i++)
            {
                var ii = i;
                tasks[i] = Task.Factory.StartNew(() =>
                  {
                      writer.Write(ii * array.Length, array, 0, array.Length);
                  }, TaskCreationOptions.LongRunning);
            }
            Task.WaitAll(tasks);
            writer.Close();

            var info = new FileInfo(tempPath);
            Assert.Equal(array.Length * parts, info.Length);

            Assert.Equal(array.Length * parts, item.Length);

            using (var reader = new BinaryReader(File.OpenRead(tempPath)))
            {
                for (var i = 0; i < parts; i++)
                    for (var j = 0; j < array.Length; j++)
                    {
                        var val = reader.ReadByte();
                        Assert.True(array[j] == val, $"{array[j]}!={val} ({i} {j})");
                    }
            }

            File.Delete(tempPath);
        }

        private bool disposedValue = false; // To detect redundant calls

        void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch (Exception e)
                    {
                        Debug.WriteLine(e);
                    }
                    writer.Dispose();
                }

                disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }


    }
}