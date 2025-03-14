﻿using System.Text.RegularExpressions;
using LibGit2Sharp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NLog;
using NLog.Config;
using NLog.Extensions.Logging;
using FeatureComparator;
using System.Runtime.CompilerServices;
using System.Reflection;

namespace GitCommitRetriever
{
    class Program
    {
        #region App Config

        private static string AppSettingsFileName = "appsettings.json";
        private static string LogConfigFileName = "NLog.config";

        #endregion

        #region Main

        static void Main(string[] args)
        {
            try
            {
                IServiceCollection services = new ServiceCollection();
                ConfigureServices(services);
                IServiceProvider serviceProvider = services.BuildServiceProvider();
                var runner = serviceProvider.GetRequiredService<Comparator>();
                runner.Run();
            }
            catch (Exception ex)
            {
                LogManager.GetCurrentClassLogger().Log(NLog.LogLevel.Fatal, $"Critical app failure: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            }
            Console.WriteLine("Program is now ended, press any key to close the window.");
            Console.ReadKey();
        }

        #endregion

        #region Services

        private static void ConfigureServices(IServiceCollection services)
        {
            IConfiguration config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory()) //From NuGet Package Microsoft.Extensions.Configuration.Json
            .AddJsonFile(AppSettingsFileName, optional: true, reloadOnChange: true)
            .Build();

            services.AddSingleton<AppConfiguration>(_X => GetAppConfiguration(config));
            services.AddLogging(loggingBuilder =>
            {
                // configure Logging with NLog
                loggingBuilder.ClearProviders();
                loggingBuilder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Trace);
                loggingBuilder.AddNLog(GetLogConfiguration());
            });
            services.AddTransient<Comparator>();
        }

        private static LoggingConfiguration GetLogConfiguration()
        {
            Stream stream = typeof(Program).Assembly.GetManifestResourceStream(Assembly.GetExecutingAssembly().GetName().Name + "." + LogConfigFileName)!;
            string xml;
            using (var reader = new StreamReader(stream))
            {
                xml = reader.ReadToEnd();
            }
            return XmlLoggingConfiguration.CreateFromXmlString(xml);
        }

        private static AppConfiguration GetAppConfiguration(IConfiguration configuration)
        {
            AppConfiguration appConfiguration = new();
            configuration.Bind(appConfiguration);
            return appConfiguration;
        }

        #endregion
    }
}