using System;
using System.IO;
using Newtonsoft.Json.Linq;
using NLog;

namespace SFTPFileMaintenanceJob
{
    class Program
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        static void Main(string[] args)
        {           
            if (args.Length == 0)
            {
                logger.Error("No process type provided. Please pass a process type as an argument.");
                Console.WriteLine("Please provide a process type as an argument.");
                return;
            }

            string processType = args[0];
            logger.Info($"Starting SFTP maintenance job for Process Type: {processType}");

            try
            {
                string jsonConfig = File.ReadAllText("AppSettings/appsettings.json");
                JObject config = JObject.Parse(jsonConfig);

                JObject processConfig = (JObject)config["SFTPSettings"]?["ProcessTypes"]?[processType];

                if (processConfig == null)
                {
                    logger.Error($"No configuration found for process type: {processType}");
                    Console.WriteLine($"No configuration found for process type: {processType}");
                    return;
                }

                SFTPMaintenance job = new SFTPMaintenance(processConfig);
                job.ExecuteMaintenanceJob();
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Error reading configuration.");
                Console.WriteLine($"Error reading configuration: {ex.Message}");
            }
        }
    }

}
