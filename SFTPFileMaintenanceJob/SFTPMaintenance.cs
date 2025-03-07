using System;
using System.IO;
using Newtonsoft.Json.Linq;
using System.Linq;
using Renci.SshNet;
using NLog;
using Renci.SshNet.Common;

namespace SFTPFileMaintenanceJob
{
    class SFTPMaintenance
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        private readonly string host;
        private readonly string username;
        private readonly string password;
        private readonly int port;
        private readonly string parentFolder;
        private readonly string archiveFolder;
        private readonly int moveThresholdDays;
        private readonly int deleteThresholdDays;

        public SFTPMaintenance(JObject processConfig)
        {
            if (processConfig == null)
                throw new ArgumentException("Invalid process configuration.");

            host = processConfig["Host"]?.ToString();
            username = processConfig["Username"]?.ToString();
            password = processConfig["Password"]?.ToString();
            port = processConfig["Port"]?.ToObject<int>() ?? 22;
            parentFolder = processConfig["ParentFolder"]?.ToString();
            archiveFolder = processConfig["ArchiveFolder"]?.ToString();
            moveThresholdDays = processConfig["MoveThresholdDays"]?.ToObject<int>() ?? 7;
            deleteThresholdDays = processConfig["DeleteThresholdDays"]?.ToObject<int>() ?? 30;
        }

        public void ExecuteMaintenanceJob()
        {
            try
            {
                using (var sftp = new SftpClient(host, port, username, password))
                {
                    sftp.Connect();
                    logger.Info($"Connected to SFTP server: {host}");

                    MoveOldFilesToArchive(sftp);
                    DeleteOldFilesFromArchive(sftp);

                    sftp.Disconnect();
                    logger.Info("SFTP maintenance job completed successfully.");
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "SFTP job failed.");
            }
        }

        private void MoveOldFilesToArchive(SftpClient sftp)
        {
            try
            {
                var moveThresholdDate = DateTime.UtcNow.AddDays(-moveThresholdDays);
                logger.Info($"Starting file movement process. Threshold date: {moveThresholdDate:yyyy-MM-dd}");

                // Process all items in the parent folder
                var items = sftp.ListDirectory(parentFolder);

                foreach (var item in items)
                {
                    // Ignore ".", ".." and "Archive" directories
                    if (item.Name == "." || item.Name == ".." || item.Name.Equals("Archive", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string sourcePath = $"{parentFolder}/{item.Name}";
                    string destinationPath = $"{archiveFolder}/{item.Name}";

                    if (item.IsDirectory)
                    {
                        // Ensure archive subdirectory exists before moving files
                        EnsureDirectoryExists(sftp, destinationPath);
                        MoveOldFilesInSubfolder(sftp, sourcePath, destinationPath, moveThresholdDate);
                    }
                    else
                    {
                        // Move files directly from the parent folder
                        MoveFile(sftp, sourcePath, destinationPath, moveThresholdDate);
                    }
                }

                logger.Info("File movement process completed successfully.");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "An error occurred while moving files to the archive.");
            }
        }

        // Ensures the subfolder exists in the archive before moving files
        private void EnsureDirectoryExists(SftpClient sftp, string directoryPath)
        {
            try
            {
                if (!sftp.Exists(directoryPath))
                {
                    sftp.CreateDirectory(directoryPath);
                    logger.Info($"Created directory: {directoryPath}");
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"Failed to create directory: {directoryPath}");
            }
        }

        // Recursively moves old files within subfolders
        private void MoveOldFilesInSubfolder(SftpClient sftp, string sourceFolder, string destinationFolder, DateTime moveThresholdDate)
        {
            try
            {
                var items = sftp.ListDirectory(sourceFolder)
                                .Where(f => f.Name != "." && f.Name != "..") // Skip system directories
                                .ToList(); // Ensure we process all files

                foreach (var item in items)
                {
                    string sourcePath = $"{sourceFolder}/{item.Name}";
                    string destinationPath = $"{destinationFolder}/{item.Name}";

                    if (item.IsDirectory)
                    {
                        // Ensure the destination subdirectory exists
                        EnsureDirectoryExists(sftp, destinationPath);
                        MoveOldFilesInSubfolder(sftp, sourcePath, destinationPath, moveThresholdDate);
                    }
                    else if (item.LastWriteTime < moveThresholdDate) // Corrected threshold check
                    {
                        MoveFile(sftp, sourcePath, destinationPath, moveThresholdDate);
                    }
                }
            }
            catch (SftpPathNotFoundException)
            {
                logger.Warn($"Directory not found: {sourceFolder}. It may have been removed already.");
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"An error occurred while processing folder: {sourceFolder}");
            }
        }

        // Moves a file and deletes the original after successful transfer
        private void MoveFile(SftpClient sftp, string sourcePath, string destinationPath, DateTime moveThresholdDate)
        {
            try
            {
                using (var fileStream = new MemoryStream())
                {
                    sftp.DownloadFile(sourcePath, fileStream);
                    fileStream.Position = 0;
                    sftp.UploadFile(fileStream, destinationPath);
                }

                sftp.DeleteFile(sourcePath);
                logger.Info($"Moved file: {sourcePath} → {destinationPath}");
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"Failed to move file: {sourcePath}");
            }
        }


        private void DeleteOldFilesFromArchive(SftpClient sftp)
        {
            try
            {
                //var deleteThresholdDate = DateTime.UtcNow.AddDays(-deleteThresholdDays);
                var deleteThresholdDate = DateTime.Now;
                logger.Info($"Starting deletion of old files in archive: {archiveFolder} (Threshold: {deleteThresholdDate:yyyy-MM-dd})");
                // Recursively delete old files inside the Archive folder
                DeleteOldFilesInFolder(sftp, archiveFolder, deleteThresholdDate);
                logger.Info("Old file deletion process completed successfully.");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "An error occurred while deleting old files from the archive.");
            }
        }

        private void DeleteOldFilesInFolder(SftpClient sftp, string folderPath, DateTime deleteThresholdDate)
        {
            try
            {
                var items = sftp.ListDirectory(folderPath)
                                .Where(f => f.Name != "." && f.Name != "..")
                                .ToList(); // Ensure we process all files without modifying the list during iteration

                foreach (var item in items)
                {
                    string itemPath = $"{folderPath}/{item.Name}";
                    if (item.IsDirectory)
                    {
                        // Recursively process subfolders
                        DeleteOldFilesInFolder(sftp, itemPath, deleteThresholdDate);
                    }
                    else
                    {
                        // Delete files older than the threshold
                        if (item.LastWriteTime < deleteThresholdDate)
                        {
                            try
                            {
                                sftp.DeleteFile(itemPath);
                                logger.Info($"Deleted file: {itemPath}");
                            }
                            catch (Exception ex)
                            {
                                logger.Error(ex, $"Failed to delete file: {itemPath}");
                            }
                        }
                    }
                }
            }
            catch (SftpPathNotFoundException ex)
            {
                logger.Warn($"Directory not found: {folderPath}. It may have been removed already.");
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"An error occurred while processing folder: {folderPath}");
            }
        }

    }
}