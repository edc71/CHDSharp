using CHDSharpLib;
using System.Data.Common;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Transactions;

namespace CHDSharpTest;

internal class Program
{
    const int threads = 4;
    const int tasks = 5;
    const bool useGC = false;
    const int useGC_seconds = 15;

    static long totalsize = 0;

    static void Main(string[] args)
    {
        CHDCommon.sw.Start();

        //CHD.TestCHD("D:\\bbh_v1.00.14a.chd");
        //Console.WriteLine($"Done:  Time = {sw.Elapsed.TotalSeconds}");
        //return;

        if (args.Length == 0)
        {
            Console.WriteLine("Expecting a Directory to Scan");
            return;
        }

        foreach (string arg in args)
        {
            string sDir = arg.Replace("\"", "");

            DirectoryInfo di = new DirectoryInfo(sDir);
            checkdir(di, true);
        }
        CHDCommon.sw.Stop();
        Console.WriteLine();
        Console.WriteLine($"Done:  Time = {CHDCommon.sw.Elapsed.TotalSeconds}");
        Console.WriteLine("Size: " + totalsize.ToString());
        Console.WriteLine((totalsize / (1024 * 1024) / CHDCommon.sw.Elapsed.TotalSeconds).ToString("F2") + " MB/s");
        Console.WriteLine("Max mem: " + CHDCommon.maxmem);
    }

    static async void checkdir(DirectoryInfo di, bool verify)
    {
        Task[] producerthread = new Task[threads];

        Queue<FileInfo> files = new Queue<FileInfo>();

        FileInfo[] fi = di.GetFiles("*.chd", SearchOption.AllDirectories);
        CHDCommon.numberfiles = fi.Length;

        totalsize = fi.Sum(f => f.Length);
        
        //fi = fi.OrderBy(f => f.Length).ToArray();
        fi = fi.OrderByDescending(f => f.Length).ToArray();
        foreach (FileInfo fi2 in fi) { files.Enqueue(fi2); }


        for (int i = 0; i < threads; i++)
        {
            producerthread[i] = Task.Factory.StartNew(() =>
            {

                while (files.Count > 0)
                {
                    FileInfo f = null;
                    lock (files)
                    {
                        f = files.Dequeue();
                    }
                    CHD chd = new CHD();
                    chd.TestCHD(chd, f.FullName, tasks);
                }
            });
        }

        bool running = true;
        Task reportthread = Task.Factory.StartNew(() =>
        {
            int garbagecounter = 0;
            while (running)
            {
                Console.Write(((TimeSpan)CHDCommon.sw.Elapsed).ToString("hh\\:mm\\:ss") + ", " + CHDCommon.processedfiles + "/" + CHDCommon.numberfiles + ", " + (CHDCommon.processedsize / (1024 * 1024) / CHDCommon.sw.Elapsed.TotalSeconds).ToString("F2") + "MB/s, " + CHDCommon.repeatedblocks + "     \r");
                Thread.Sleep(1000);
                if (useGC)
                    if (++garbagecounter == useGC_seconds)
                    {
                        GC.Collect();
                        garbagecounter = 0;
                    }
            }
        });

        for (int i = 0; i < threads; i++)
        {
            Task.WaitAll(producerthread[i]);
        }
        running = false;
    }
}