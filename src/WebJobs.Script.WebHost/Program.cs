﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Linq;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Azure.WebJobs.Script.Config;
using Microsoft.Azure.WebJobs.Script.WebHost.Configuration;
using Microsoft.Azure.WebJobs.Script.WebHost.DependencyInjection;
using Microsoft.Azure.WebJobs.Script.WebHost.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using DataProtectionConstants = Microsoft.Azure.Web.DataProtection.Constants;

namespace Microsoft.Azure.WebJobs.Script.WebHost
{
    public class Program
    {
        public static void Main(string[] args)
        {
            InitializeProcess();

            var host = BuildWebHost(args);

            host.RunAsync()
                .Wait();
        }

        public static IWebHost BuildWebHost(string[] args)
        {
            return CreateWebHostBuilder(args).UseIIS().Build();
        }

        public static IWebHostBuilder CreateWebHostBuilder(string[] args = null)
        {
// Setting this env variable to test placeholder scenarios locally.
#if PLACEHOLDERSIMULATION
            SystemEnvironment.Instance.SetEnvironmentVariable(EnvironmentSettingNames.AzureWebsitePlaceholderMode, "1");
            SystemEnvironment.Instance.SetEnvironmentVariable(EnvironmentSettingNames.AzureWebsiteContainerReady, "0");
#endif

            return AspNetCore.WebHost.CreateDefaultBuilder(args)
                .ConfigureKestrel(o =>
                {
                    o.Limits.MaxRequestBodySize = ScriptConstants.DefaultMaxRequestBodySize;
                })
                .UseSetting(WebHostDefaults.EnvironmentKey, Environment.GetEnvironmentVariable(EnvironmentSettingNames.EnvironmentNameKey))
                .ConfigureServices(services =>
                {
                    services.Configure<IISServerOptions>(o =>
                    {
                        o.MaxRequestBodySize = ScriptConstants.DefaultMaxRequestBodySize;
                    });
                })
                .ConfigureAppConfiguration((builderContext, config) =>
                {
                    // replace the default environment source with our own
                    IConfigurationSource envVarsSource = config.Sources.OfType<EnvironmentVariablesConfigurationSource>().FirstOrDefault();
                    if (envVarsSource != null)
                    {
                        config.Sources.Remove(envVarsSource);
                    }

                    config.Add(new ScriptEnvironmentVariablesConfigurationSource());

                    config.Add(new WebScriptHostConfigurationSource
                    {
                        IsAppServiceEnvironment = SystemEnvironment.Instance.IsAppService(),
                        IsLinuxContainerEnvironment = SystemEnvironment.Instance.IsAnyLinuxConsumption(),
                        IsLinuxAppServiceEnvironment = SystemEnvironment.Instance.IsLinuxAppService()
                    });
                    config.Add(new FunctionsHostingConfigSource(SystemEnvironment.Instance));
                })
                .ConfigureLogging((context, loggingBuilder) =>
                {
                    loggingBuilder.ClearProviders();

                    loggingBuilder.AddDefaultWebJobsFilters();
                    loggingBuilder.AddWebJobsSystem<WebHostSystemLoggerProvider>();
                })
                .UseStartup<Startup>();
        }

        /// <summary>
        /// Perform any process level initialization that needs to happen BEFORE
        /// the WebHost is initialized.
        /// </summary>
        private static void InitializeProcess()
        {
            if (SystemEnvironment.Instance.IsLinuxConsumptionOnAtlas())
            {
                AppDomain.CurrentDomain.UnhandledException += CurrentDomainOnUnhandledExceptionInLinuxConsumption;
            }
            else if (SystemEnvironment.Instance.IsFlexConsumptionSku())
            {
                //todo: Replace with legion specific logger.
                AppDomain.CurrentDomain.UnhandledException += CurrentDomainOnUnhandledExceptionInLinuxConsumption;
            }
            else if (SystemEnvironment.Instance.IsLinuxAppService())
            {
                AppDomain.CurrentDomain.UnhandledException += CurrentDomainOnUnhandledExceptionInLinuxAppService;
            }

            // Some environments only set the auth key. Ensure that is used as the encryption key if that is not set
            string authEncryptionKey = SystemEnvironment.Instance.GetEnvironmentVariable(EnvironmentSettingNames.WebSiteAuthEncryptionKey);
            if (authEncryptionKey != null &&
                SystemEnvironment.Instance.GetEnvironmentVariable(DataProtectionConstants.AzureWebsiteEnvironmentMachineKey) == null)
            {
                SystemEnvironment.Instance.SetEnvironmentVariable(DataProtectionConstants.AzureWebsiteEnvironmentMachineKey, authEncryptionKey);
            }

            ConfigureMinimumThreads(SystemEnvironment.Instance);
        }

        private static void CurrentDomainOnUnhandledExceptionInLinuxConsumption(object sender, UnhandledExceptionEventArgs e)
        {
            // Fallback console logs in case kusto logging fails.
            Console.WriteLine($"{nameof(CurrentDomainOnUnhandledExceptionInLinuxConsumption)}: {e.ExceptionObject}");

            LinuxContainerEventGenerator.LogUnhandledException((Exception)e.ExceptionObject);
        }

        private static void CurrentDomainOnUnhandledExceptionInLinuxAppService(object sender, UnhandledExceptionEventArgs e)
        {
            LinuxAppServiceEventGenerator.LogUnhandledException((Exception)e.ExceptionObject);
        }

        private static void ConfigureMinimumThreads(IEnvironment environment)
        {
            // For information on MinThreads, see:
            // https://docs.microsoft.com/en-us/dotnet/api/system.threading.threadpool.setminthreads?view=netcore-2.2
            // https://docs.microsoft.com/en-us/azure/redis-cache/cache-faq#important-details-about-threadpool-growth
            // https://blogs.msdn.microsoft.com/perfworld/2010/01/13/how-can-i-improve-the-performance-of-asp-net-by-adjusting-the-clr-thread-throttling-properties/
            //
            // This behavior can be overridden by using the "ComPlus_ThreadPool_ForceMinWorkerThreads" environment variable (honored by the .NET threadpool).

            var effectiveCores = environment.GetEffectiveCoresCount();

            // This value was derived by looking at the thread count for several function apps running load on a multicore machine and dividing by the number of cores.
            const int minThreadsPerLogicalProcessor = 6;

            int minThreadCount = effectiveCores * minThreadsPerLogicalProcessor;
            ThreadPool.SetMinThreads(minThreadCount, minThreadCount);
        }
    }
}