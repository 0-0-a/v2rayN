﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

using v2rayN.Mode;
using v2rayN.Protos.Statistics;

using Grpc.Core;

namespace v2rayN.Handler
{
    class StatisticsHandler
    {
        private Mode.Config config_;

        private Channel channel_;
        private StatsService.StatsServiceClient client_;
        private Thread workThread_;

        Action<ulong, ulong, ulong, ulong, List<Mode.ServerStatistics>> updateFunc_;

        private bool enabled_;
        public bool Enable 
        {
            get { return enabled_; }
            set { enabled_ = value; }
        }

        public bool UpdateUI;

        public ulong TotalUp { get; private set; }
       
        public ulong TotalDown { get; private set; }

        public List<Mode.ServerStatistics> Statistic{ get; set; }

        public ulong Up { get; private set; }

        public ulong Down { get; private set; }

        private string logPath_;

        private bool exitFlag_;  // true to close workThread_

        public StatisticsHandler(Mode.Config config, Action<ulong, ulong, ulong, ulong, List<Mode.ServerStatistics>> update)
        {
            config_ = config;
            enabled_ = config.enableStatistics;
            UpdateUI = false;
            updateFunc_ = update;
            logPath_ = Utils.GetPath($"{Global.StatisticLogDirectory}\\");
            Statistic = new List<Mode.ServerStatistics>();
            exitFlag_ = false;

            DeleteExpiredLog();
            foreach (var server in config.vmess)
            {
                var statistic = new ServerStatistics(server.remarks, server.address, server.port, server.path, server.requestHost, 0, 0, 0, 0);
                Statistic.Add(statistic);
            }

            loadFromFile();

            grpcInit();

            workThread_ = new Thread(new ThreadStart(run));
            workThread_.Start();
        }

        private void grpcInit ()
        {
            channel_ = new Channel($"127.0.0.1:{Global.InboundAPIPort}", ChannelCredentials.Insecure);
            channel_.ConnectAsync();
            client_ = new StatsService.StatsServiceClient(channel_);
        }

        public void Close()
        {
            exitFlag_ = true;
            channel_.ShutdownAsync();
        }

        public void run()
        {
            try
            {
                while (!exitFlag_)
                {
                    if (enabled_ && channel_.State == ChannelState.Ready)
                    {
                        var res = client_.QueryStats(new QueryStatsRequest() { Pattern = "", Reset = true });

                        var addr = config_.address();
                        var port = config_.port();
                        var cur = Statistic.FindIndex(item => item.address == addr && item.port == port);
                        ulong up = 0,
                             down = 0;

                        //TODO: parse output
                        parseOutput(res.Stat, out up, out down);

                        Up = up;
                        Down = down;

                        TotalUp += up;
                        TotalDown += down;

                        if(cur != -1)
                        {
                            Statistic[cur].todayUp += up;
                            Statistic[cur].todayDown += down;
                            Statistic[cur].totalUp += up;
                            Statistic[cur].totalDown += down;
                        }

                        if (UpdateUI)
                            updateFunc_(TotalUp, TotalDown, Up, Down, Statistic);
                        Thread.Sleep(config_.statisticsFreshRate);
                        channel_.ConnectAsync();
                    }
                }
            }
            catch {  }
        }

        public void parseOutput(Google.Protobuf.Collections.RepeatedField<Stat> source, out ulong up, out ulong down)
        {

            up = 0; down = 0;

            foreach(var stat in source)
            {
                var name = stat.Name;
                var value = stat.Value;
                var nStr = name.Split(">>>".ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
                var type = "";

                name = name.Trim();

                name = nStr[1];
                type = nStr[3];

                if (name == Global.InboundProxyTagName)
                {
                    if (type == "uplink")
                    {
                        up = (ulong)value;
                    }
                    else if (type == "downlink")
                    {
                        down = (ulong)value;
                    }
                }
            }
        }

        public void saveToFile()
        {
            if(!Directory.Exists(logPath_))
            {
                Directory.CreateDirectory(logPath_);
            }

            // 总流量统计文件
            var overallPath = Path.Combine(logPath_, Global.StatisticLogOverall);
            if (!File.Exists(overallPath))
            {
                File.Create(overallPath);
            }
            try
            {
                using (var overallWriter = new StreamWriter(overallPath))
                {
                    double up_amount, down_amount;
                    string up_unit, down_unit;

                    Utils.ToHumanReadable(TotalUp, out up_amount, out up_unit);
                    Utils.ToHumanReadable(TotalDown, out down_amount, out down_unit);

                    overallWriter.WriteLine($"LastUpdate {DateTime.Now.ToLongDateString()} {DateTime.Now.ToLongTimeString()}");
                    overallWriter.WriteLine($"UP {string.Format("{0:f2}", up_amount)}{up_unit} {TotalUp}");
                    overallWriter.WriteLine($"DOWN {string.Format("{0:f2}", down_amount)}{down_unit} {TotalDown}");
                    foreach(var s in Statistic)
                    {
                        overallWriter.WriteLine($"* {s.name} {s.address} {s.port} {s.path} {s.host} {s.totalUp} {s.totalDown}");
                    }
                }
            }
            catch { }

            // 当天流量记录文件
            var dailyPath = Path.Combine(logPath_, $"{DateTime.Now.ToLongDateString()}.txt");
            if (!File.Exists(dailyPath))
            {
                File.Create(dailyPath);
            }
            try
            {
                using (var dailyWriter = new StreamWriter(dailyPath))
                {
                    dailyWriter.WriteLine($"LastUpdate {DateTime.Now.ToLongDateString()} {DateTime.Now.ToLongTimeString()}");
                    foreach (var s in Statistic)
                    {
                        dailyWriter.WriteLine($"* {s.name} {s.address} {s.port} {s.path} {s.host} {s.todayUp} {s.todayDown}");
                    }
                }
            }
            catch { }
        }

        public void loadFromFile()
        {
            if (!Directory.Exists(logPath_)) return;

            // 总流量统计文件
            ///
            /// 文件结构
            /// LastUpdate [date] [time]
            /// UP [readable string] [amount]
            /// DOWN [readable string] [amount]
            /// 每行每个数据空格分隔
            ///
            var overallPath = Path.Combine(logPath_, Global.StatisticLogOverall);
            if (File.Exists(overallPath))
            {
                try
                {
                    using (var overallReader = new StreamReader(overallPath))
                    {
                        while (!overallReader.EndOfStream)
                        {
                            var line = overallReader.ReadLine();
                            if (line.StartsWith("LastUpdate"))
                            {

                            }
                            else if (line.StartsWith("UP"))
                            {
                                var datas = line.Split(' ');
                                if (datas.Length < 3) return;
                                TotalUp = ulong.Parse(datas[2]);
                            }
                            else if (line.StartsWith("DOWN"))
                            {
                                var datas = line.Split(' ');
                                if (datas.Length < 3) return;
                                TotalDown = ulong.Parse(datas[2]);
                            }
                            else if (line.StartsWith("*"))
                            {
                                var datas = line.Split(' ');
                                if (datas.Length < 8) return;
                                var name = datas[1];
                                var address = datas[2];
                                var port = int.Parse(datas[3]);
                                var path = datas[4];
                                var host = datas[5];
                                var totalUp = ulong.Parse(datas[6]);
                                var totalDown = ulong.Parse(datas[7]);

                                var temp = new ServerStatistics(name, address, port, path, host, 0, 0, 0, 0);
                                var index = Statistic.FindIndex(item => Utils.IsIdenticalServer(item, temp));
                                if (index != -1)
                                {
                                    Statistic[index].totalUp = totalUp;
                                    Statistic[index].totalDown = totalDown;
                                }
                                else
                                {
                                    var s = new Mode.ServerStatistics(name, address, port, path, host, totalUp, totalDown, 0, 0);
                                    Statistic.Add(s);
                                }
                            }
                        }
                    }
                }
                catch { }  
            }

            // 当天流量记录文件
            var dailyPath = Path.Combine(logPath_, $"{DateTime.Now.ToLongDateString()}.txt");
            if (File.Exists(dailyPath))
            {
                try
                {
                    using (var dailyReader = new StreamReader(dailyPath))
                    {
                        while (!dailyReader.EndOfStream)
                        {
                            var line = dailyReader.ReadLine();
                            if (line.StartsWith("LastUpdate"))
                            {

                            }
                            else if (line.StartsWith("*"))
                            {
                                var datas = line.Split(' ');
                                if (datas.Length < 8) return;
                                var name = datas[1];
                                var address = datas[2];
                                var port = int.Parse(datas[3]);
                                var path = datas[4];
                                var host = datas[5];
                                var todayUp = ulong.Parse(datas[6]);
                                var todayDown = ulong.Parse(datas[7]);

                                var temp = new ServerStatistics(name, address, port, path, host, 0, 0, 0, 0);
                                var index = Statistic.FindIndex(item => Utils.IsIdenticalServer(item, temp));
                                if (index != -1)
                                {
                                    Statistic[index].todayUp = todayUp;
                                    Statistic[index].todayDown = todayDown;
                                }
                                else
                                {
                                    var s = new Mode.ServerStatistics(name, address, port, path, host, 0, 0, todayUp, todayDown);
                                    Statistic.Add(s);
                                }
                            }
                        }
                    }
                }
                catch { }
            }
        }


        private void DeleteExpiredLog()
        {
            if (!Directory.Exists(logPath_)) return;
            var dirInfo = new DirectoryInfo(logPath_);
            var files = dirInfo.GetFiles();
            foreach (var file in files)
            {
                if (file.Name == "overall.txt") continue;
                var name = file.Name.Split('.')[0];
                var ft = DateTime.Parse(name);
                var ct = DateTime.Now;
                var dur = ct - ft;
                if(dur.Days > config_.CacheDays)
                {
                    file.Delete();
                }
            }
        }
    }
}
