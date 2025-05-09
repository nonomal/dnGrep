using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using dnGREP.Common.IO;
using dnGREP.Common.UI;
using dnGREP.Everything;
using dnGREP.Localization;
using NLog;
using UtfUnknown;
using Resources = dnGREP.Localization.Properties.Resources;

namespace dnGREP.Common
{
    public static partial class Utils
    {
        public const string defaultCacheFolderName = "dnGrep-files";

        private const string metacharacters = "+()^$.{}|\\";

        private const int ErrorRequiresElevation = 740;

        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        private static readonly char[] chars =
            "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ1234567890".ToCharArray();

        private static readonly string tempFolderName;
        private static readonly string undoFolderName;

        private static readonly object regexLock = new();
        private static readonly Dictionary<string, Regex> regexCache = [];

        static Utils()
        {
            tempFolderName = "dnGrep-temp-" + GetUniqueKey(12);
            undoFolderName = "dnGrep-undo-" + GetUniqueKey(12);
        }

        /// <summary>
        /// Copies the folder recursively. Uses includePattern to avoid unnecessary objects
        /// </summary>
        /// <param name="sourceDirectory"></param>
        /// <param name="destinationDirectory"></param>
        /// <param name="includePattern">Regex pattern that matches file or folder to be included. If null or empty, the parameter is ignored</param>
        /// <param name="excludePattern">Regex pattern that matches file or folder to be included. If null or empty, the parameter is ignored</param>
        public static void CopyFiles(string sourceDirectory, string destinationDirectory, string? includePattern, string? excludePattern)
        {
            if (!Directory.Exists(destinationDirectory)) Directory.CreateDirectory(destinationDirectory);

            var files = Directory.GetFileSystemEntries(sourceDirectory);

            foreach (string element in files)
            {
                if (!string.IsNullOrEmpty(includePattern) && File.Exists(element) && !Regex.IsMatch(element, includePattern))
                    continue;

                if (!string.IsNullOrEmpty(excludePattern) && File.Exists(element) && Regex.IsMatch(element, excludePattern))
                    continue;

                // Sub directories
                if (Directory.Exists(element))
                    CopyFiles(element, Path.Combine(destinationDirectory, Path.GetFileName(element)), includePattern, excludePattern);
                // Files in directory
                else
                    CopyFile(element, Path.Combine(destinationDirectory, Path.GetFileName(element)), true);
            }
        }

        /// <summary>
        /// Copies files with directory structure based on search results. If destination folder does not exist, creates it.
        /// </summary>
        /// <param name="source"></param>
        /// <param name="sourceDirectory"></param>
        /// <param name="destinationDirectory"></param>
        /// <param name="action"></param>
        /// <returns>number of files copied</returns>
        public static int CopyFiles(List<GrepSearchResult> source, string sourceDirectory, string destinationDirectory, OverwriteFile action)
        {
            return CopyMoveFilesImpl(source, sourceDirectory, destinationDirectory, action, false).count;
        }

        /// <summary>
        /// Moves files with directory structure based on search results. If destination folder does not exist, creates it.
        /// </summary>
        /// <param name="source"></param>
        /// <param name="sourceDirectory"></param>
        /// <param name="destinationDirectory"></param>
        /// <param name="action"></param>
        /// <returns>count of copied/moved files and List of real files moved</returns>
        /// <remarks>
        /// The list contains only real files that were moved, and the count also includes files copied from archives
        /// </remarks>
        public static (int count, List<string> realFilesMoved) MoveFiles(List<GrepSearchResult> source, string sourceDirectory, string destinationDirectory, OverwriteFile action)
        {
            return CopyMoveFilesImpl(source, sourceDirectory, destinationDirectory, action, true);
        }

        /// <summary>
        /// Moves files with directory structure based on search results. If destination folder does not exist, creates it.
        /// </summary>
        /// <param name="source"></param>
        /// <param name="sourceDirectory"></param>
        /// <param name="destinationDirectory"></param>
        /// <param name="action"></param>
        /// <param name="deleteAfterCopy">true to move files, false to copy</param>
        /// <returns>count of copied/moved files and List of real files moved</returns>
        /// <remarks>
        /// The list contains only real files that were moved, and the count also includes files copied from archives
        /// </remarks>
        private static (int count, List<string> realFilesMoved) CopyMoveFilesImpl(
            List<GrepSearchResult> source, string sourceDirectory, string destinationDirectory,
            OverwriteFile action, bool deleteAfterCopy)
        {
            sourceDirectory = FixFolderName(sourceDirectory);
            destinationDirectory = FixFolderName(destinationDirectory);

            if (!Directory.Exists(destinationDirectory)) Directory.CreateDirectory(destinationDirectory);

            int count = 0;
            HashSet<string> files = [];
            List<string> realFilesMoved = [];

            bool copyFilesFromArchive = deleteAfterCopy ?
               GrepSettings.Instance.Get<ArchiveCopyMoveDelete>(GrepSettings.Key.ArchiveMove) == ArchiveCopyMoveDelete.CopyFile :
               GrepSettings.Instance.Get<ArchiveCopyMoveDelete>(GrepSettings.Key.ArchiveCopy) == ArchiveCopyMoveDelete.CopyFile;

            foreach (GrepSearchResult result in source)
            {
                if (copyFilesFromArchive && IsArchive(result.FileNameReal) && !files.Contains(result.FileNameDisplayed) &&
                    result.FileNameReal.Contains(sourceDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    files.Add(result.FileNameDisplayed);
                    string tempFile = ArchiveDirectory.ExtractToTempFile(result);

                    FileInfo sourceFileInfo = new(tempFile);
                    string destinationPath = string.Concat(destinationDirectory, result.FileNameReal.AsSpan(sourceDirectory.Length));
                    destinationPath = Path.ChangeExtension(destinationPath, null);

                    int pos = result.FileNameDisplayed.IndexOf(ArchiveDirectory.ArchiveSeparator, StringComparison.Ordinal);
                    if (pos == -1) continue; // should never happen
                    pos += ArchiveDirectory.ArchiveSeparator.Length;
                    string subPath = result.FileNameDisplayed[pos..];
                    destinationPath = Path.Combine(destinationPath, subPath);
                    FileInfo destinationFileInfo = new(destinationPath);
                    if (sourceFileInfo.FullName != destinationFileInfo.FullName)
                    {
                        bool overwrite = action == OverwriteFile.Yes;
                        if (destinationFileInfo.Exists && !string.IsNullOrEmpty(destinationFileInfo.DirectoryName))
                        {
                            if (action == OverwriteFile.Prompt &&
                                !AskUserOverwrite(destinationFileInfo.Name, destinationFileInfo.DirectoryName,
                                false, ref overwrite, ref action))
                            {
                                return (count, realFilesMoved);
                            }

                            if (!overwrite)
                            {
                                continue;
                            }
                        }

                        // Move is the same as Copy from an archive; the archive does not get modified
                        CopyFile(sourceFileInfo.FullName, destinationFileInfo.FullName, overwrite);
                        DeleteFile(tempFile);
                        count++;
                    }
                }
                else if (!files.Contains(result.FileNameReal) && result.FileNameReal.Contains(sourceDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    files.Add(result.FileNameReal);
                    FileInfo sourceFileInfo = new(result.FileNameReal);
                    FileInfo destinationFileInfo = new(string.Concat(destinationDirectory, result.FileNameReal.AsSpan(sourceDirectory.Length)));
                    if (sourceFileInfo.FullName != destinationFileInfo.FullName)
                    {
                        bool overwrite = action == OverwriteFile.Yes;
                        if (destinationFileInfo.Exists && !string.IsNullOrEmpty(destinationFileInfo.DirectoryName))
                        {
                            if (action == OverwriteFile.Prompt &&
                                !AskUserOverwrite(destinationFileInfo.Name, destinationFileInfo.DirectoryName,
                                deleteAfterCopy, ref overwrite, ref action))
                            {
                                return (count, realFilesMoved);
                            }

                            if (!overwrite)
                            {
                                continue;
                            }
                        }

                        CopyFile(sourceFileInfo.FullName, destinationFileInfo.FullName, overwrite);
                        if (deleteAfterCopy)
                        {
                            realFilesMoved.Add(sourceFileInfo.FullName);
                            DeleteFile(sourceFileInfo.FullName);
                        }
                        count++;
                    }
                }
            }
            return (count, realFilesMoved);
        }

        /// <summary>
        /// Copies source files to destination folder without source directory structure.
        /// If destination folder does not exist, creates it.
        /// </summary>
        /// <param name="source"></param>
        /// <param name="destinationDirectory"></param>
        /// <param name="action"></param>
        /// <returns>number of files copied</returns>
        public static int CopyFiles(List<GrepSearchResult> source, string destinationDirectory, OverwriteFile action)
        {
            return CopyMoveImpl(source, destinationDirectory, action, false).count;
        }

        /// <summary>
        /// Moves source files to destination folder without source directory structure.
        /// If destination folder does not exist, creates it.
        /// </summary>
        /// <param name="source"></param>
        /// <param name="destinationDirectory"></param>
        /// <param name="action"></param>
        /// <returns>number of files moved</returns>
        public static (int count, List<string> realFilesMoved) MoveFiles(List<GrepSearchResult> source, string destinationDirectory, OverwriteFile action)
        {
            return CopyMoveImpl(source, destinationDirectory, action, true);
        }

        private static (int count, List<string> realFilesMoved) CopyMoveImpl(List<GrepSearchResult> source, string destinationDirectory, OverwriteFile action, bool deleteAfterCopy)
        {
            if (!Directory.Exists(destinationDirectory)) Directory.CreateDirectory(destinationDirectory);

            int count = 0;
            HashSet<string> files = [];
            List<string> realFilesMoved = [];

            bool copyFilesFromArchive = deleteAfterCopy ?
                GrepSettings.Instance.Get<ArchiveCopyMoveDelete>(GrepSettings.Key.ArchiveMove) == ArchiveCopyMoveDelete.CopyFile :
                GrepSettings.Instance.Get<ArchiveCopyMoveDelete>(GrepSettings.Key.ArchiveCopy) == ArchiveCopyMoveDelete.CopyFile;

            foreach (GrepSearchResult result in source)
            {
                if (copyFilesFromArchive && IsArchive(result.FileNameReal) && !files.Contains(result.FileNameDisplayed))
                {
                    files.Add(result.FileNameDisplayed);
                    string tempFile = ArchiveDirectory.ExtractToTempFile(result);
                    FileInfo sourceFileInfo = new(tempFile);
                    FileInfo destinationFileInfo = new(Path.Combine(destinationDirectory, Path.GetFileName(tempFile)));
                    if (sourceFileInfo.FullName != destinationFileInfo.FullName)
                    {
                        bool overwrite = action == OverwriteFile.Yes;
                        if (destinationFileInfo.Exists)
                        {
                            if (action == OverwriteFile.Prompt && !string.IsNullOrEmpty(destinationFileInfo.DirectoryName) &&
                                !AskUserOverwrite(destinationFileInfo.Name, destinationFileInfo.DirectoryName,
                                    deleteAfterCopy, ref overwrite, ref action))
                            {
                                return (count, realFilesMoved);
                            }

                            if (!overwrite)
                            {
                                continue;
                            }
                        }

                        // Move is the same as Copy from an archive; the archive does not get modified
                        CopyFile(sourceFileInfo.FullName, destinationFileInfo.FullName, overwrite);
                        DeleteFile(tempFile);
                        count++;
                    }

                }
                else if (!files.Contains(result.FileNameReal))
                {
                    files.Add(result.FileNameReal);
                    FileInfo sourceFileInfo = new(result.FileNameReal);
                    FileInfo destinationFileInfo = new(Path.Combine(destinationDirectory, Path.GetFileName(result.FileNameReal)));
                    if (sourceFileInfo.FullName != destinationFileInfo.FullName)
                    {
                        bool overwrite = action == OverwriteFile.Yes;
                        if (destinationFileInfo.Exists)
                        {
                            if (action == OverwriteFile.Prompt && !string.IsNullOrEmpty(destinationFileInfo.DirectoryName) &&
                                !AskUserOverwrite(destinationFileInfo.Name, destinationFileInfo.DirectoryName,
                                    deleteAfterCopy, ref overwrite, ref action))
                            {
                                return (count, realFilesMoved);
                            }

                            if (!overwrite)
                            {
                                continue;
                            }
                        }

                        CopyFile(sourceFileInfo.FullName, destinationFileInfo.FullName, overwrite);
                        if (deleteAfterCopy)
                        {
                            realFilesMoved.Add(sourceFileInfo.FullName);
                            DeleteFile(sourceFileInfo.FullName);
                        }
                        count++;
                    }
                }
            }
            return (count, realFilesMoved);
        }

        /// <summary>
        /// Returns true if destinationDirectory is not included in source files
        /// </summary>
        /// <param name="source"></param>
        /// <param name="destinationDirectory"></param>
        /// <returns></returns>
        public static bool CanCopyFiles(List<GrepSearchResult>? source, string? destinationDirectory)
        {
            if (destinationDirectory == null || source == null || source.Count == 0)
                return false;

            destinationDirectory = FixFolderName(destinationDirectory);

            HashSet<string> files = [];

            foreach (GrepSearchResult result in source)
            {
                if (!files.Contains(result.FileNameReal))
                {
                    files.Add(result.FileNameReal);
                    FileInfo sourceFileInfo = new(result.FileNameReal);
                    FileInfo destinationFileInfo = new(destinationDirectory + Path.GetFileName(result.FileNameReal));
                    if (sourceFileInfo.FullName == destinationFileInfo.FullName)
                        return false;
                }
            }

            return true;
        }

        private static bool AskUserOverwrite(string fileName, string directoryName, bool deleteAfterCopy,
            ref bool overwrite, ref OverwriteFile action)
        {
            var answer = CustomMessageBox.Show(
                TranslationSource.Format(Resources.MessageBox_TheFile0AlreadyExistsIn1OverwriteExisting, fileName, directoryName),
                Resources.MessageBox_DnGrep,
                MessageBoxButtonEx.YesAllNoAllCancel, MessageBoxImage.Question,
                MessageBoxResultEx.No, MessageBoxCustoms.DoNotAskAgain,
                TranslationSource.Instance.FlowDirection);

            if (answer.Result == MessageBoxResultEx.Cancel)
            {
                return false;
            }
            else if (answer.Result == MessageBoxResultEx.No)
            {
                overwrite = false;

                if (answer.DoNotAskAgain)
                {
                    // set the action to overwrite:no for the remainder of the set of files
                    action = OverwriteFile.No;

                    // set user option to no for future operations
                    string key = deleteAfterCopy ? GrepSettings.Key.OverwriteFilesOnMove : GrepSettings.Key.OverwriteFilesOnCopy;
                    GrepSettings.Instance.Set(key, OverwriteFile.No);
                }
            }
            else if (answer.Result == MessageBoxResultEx.NoToAll)
            {
                overwrite = false;
                // set the action to overwrite:no for the remainder of the set of files
                action = OverwriteFile.No;

                if (answer.DoNotAskAgain)
                {
                    // set user option to no for future operations
                    string key = deleteAfterCopy ? GrepSettings.Key.OverwriteFilesOnMove : GrepSettings.Key.OverwriteFilesOnCopy;
                    GrepSettings.Instance.Set(key, OverwriteFile.No);
                }
            }
            else if (answer.Result == MessageBoxResultEx.Yes)
            {
                overwrite = true;

                if (answer.DoNotAskAgain)
                {
                    // set the action to overwrite:yes for the remainder of the set of files
                    action = OverwriteFile.Yes;

                    // set user option to yes for future operations
                    string key = deleteAfterCopy ? GrepSettings.Key.OverwriteFilesOnMove : GrepSettings.Key.OverwriteFilesOnCopy;
                    GrepSettings.Instance.Set(key, OverwriteFile.Yes);
                }
            }
            else if (answer.Result == MessageBoxResultEx.YesToAll)
            {
                overwrite = true;
                // set the action to overwrite:yes for the remainder of the set of files
                action = OverwriteFile.Yes;

                if (answer.DoNotAskAgain)
                {
                    // set user option to yes for future operations
                    string key = deleteAfterCopy ? GrepSettings.Key.OverwriteFilesOnMove : GrepSettings.Key.OverwriteFilesOnCopy;
                    GrepSettings.Instance.Set(key, OverwriteFile.Yes);
                }
            }
            return true;
        }

        /// <summary>
        /// Deletes file based on search results. 
        /// </summary>
        /// <param name="source"></param>
        public static List<string> DeleteFiles(List<GrepSearchResult> source)
        {
            bool deleteArchive = GrepSettings.Instance.Get<ArchiveCopyMoveDelete>(GrepSettings.Key.ArchiveDelete)
                == ArchiveCopyMoveDelete.WholeArchive;

            HashSet<string> files = [];
            foreach (GrepSearchResult result in source)
            {
                // based on option, do not delete archives
                if (IsArchive(result.FileNameReal) && !deleteArchive)
                    continue;

                if (!files.Contains(result.FileNameReal))
                {
                    files.Add(result.FileNameReal);

                    DeleteFile(result.FileNameReal);
                }
            }
            return [.. files];
        }

        /// <summary>
        /// Deletes files to the recycle bin based on search results. 
        /// </summary>
        /// <param name="source"></param>
        public static List<string> SendToRecycleBin(List<GrepSearchResult> source)
        {
            bool deleteArchive = GrepSettings.Instance.Get<ArchiveCopyMoveDelete>(GrepSettings.Key.ArchiveDelete)
                == ArchiveCopyMoveDelete.WholeArchive;

            HashSet<string> files = [];
            foreach (GrepSearchResult result in source)
            {
                // based on option, do not delete archives
                if (IsArchive(result.FileNameReal) && !deleteArchive)
                    continue;

                if (!files.Contains(result.FileNameReal))
                {
                    files.Add(result.FileNameReal);

                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(result.FileNameReal,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }
            }
            return [.. files];
        }

        /// <summary>
        /// Copies file. If folder does not exist, creates it.
        /// </summary>
        /// <param name="sourcePath"></param>
        /// <param name="destinationPath"></param>
        /// <param name="overWrite"></param>
        public static void CopyFile(string sourcePath, string destinationPath, bool overWrite)
        {
            if (File.Exists(destinationPath) && !overWrite)
                throw new IOException($"File: '{destinationPath}' exists.");

            FileInfo destinationFileInfo = new(destinationPath);
            if (destinationFileInfo.Directory != null && !destinationFileInfo.Directory.Exists)
            {
                destinationFileInfo.Directory.Create();
            }

            File.Copy(sourcePath, destinationPath, overWrite);
        }

        /// <summary>
        /// Deletes files even if they are read only
        /// </summary>
        /// <param name="path"></param>
        public static void DeleteFile(string path)
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }

        /// <summary>
        /// Deletes folder even if it contains read only files
        /// </summary>
        /// <param name="path"></param>
        public static void DeleteFolder(string path)
        {
            string[] files = GetFileList(path, "*.*", string.Empty, false, false, true, true, true, false, false, 0, 0, FileDateFilter.None, null, null, false, -1, true, string.Empty, default);
            foreach (string file in files)
            {
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }
            Directory.Delete(path, true);
        }

        /// <summary>
        /// Detects the byte order mark of a file and returns
        /// an appropriate encoding for the file.
        /// </summary>
        /// <param name="srcFile"></param>
        /// <returns></returns>
        public static Encoding GetFileEncoding(string srcFile)
        {
            using FileStream readStream = File.Open(srcFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var results = CharsetDetector.DetectFromStream(readStream);
            // Get the best Detection
            DetectionDetail resultDetected = results.Detected;
            // Get the System.Text.Encoding of the found encoding (can be null if not available)
            Encoding encoding = resultDetected?.Encoding ?? Encoding.Default;
            encoding = CheckForUnicodeWithNoBOM(encoding, resultDetected, readStream);
            return encoding;
        }

        /// <summary>
        /// Detects the byte order mark of a file and returns an appropriate encoding for the file.
        /// </summary>
        /// <param name="stream"></param>
        /// <returns></returns>
        public static Encoding GetFileEncoding(Stream stream)
        {
            var results = CharsetDetector.DetectFromStream(stream);
            // Get the best Detection
            DetectionDetail resultDetected = results.Detected;
            // Get the System.Text.Encoding of the found encoding (can be null if not available)
            Encoding encoding = resultDetected?.Encoding ?? Encoding.Default;
            encoding = CheckForUnicodeWithNoBOM(encoding, resultDetected, stream);

            // reset the stream back to the beginning
            stream.Seek(0, SeekOrigin.Begin);
            return encoding;
        }

        private static Encoding CheckForUnicodeWithNoBOM(Encoding encoding, DetectionDetail? resultDetected,
            Stream readStream)
        {
            if (resultDetected != null)
            {
                if (resultDetected.Confidence >= 0.5f &&
                    (resultDetected.HasBOM ||
                    resultDetected.EncodingName.Equals("utf-8", StringComparison.OrdinalIgnoreCase)))
                {
                    return encoding;
                }
            }

            readStream.Seek(0, SeekOrigin.Begin);
            byte[] buff = new byte[1024];
            int count = readStream.Read(buff, 0, buff.Length);

            // LE: 20 00  0D 00  0A 00
            // BE: 00 20  00 0D  00 0A

            int le16Count = 0, be16Count = 0;
            int le32Count = 0, be32Count = 0;
            for (int i = 0; i < count - 1; i += 2)
            {
                byte b1 = buff[i];
                byte b2 = buff[i + 1];
                if (b2 == 0x0 && ((b1 >= 0x20 && b1 <= 0x7E) || b1 == 0x0A || b1 == 0x0D))
                {
                    // check for UTF-32
                    if (i < count - 3 && buff[i + 2] == 0x0 && buff[i + 3] == 0x0)
                    {
                        le32Count++;
                    }
                    else
                    {
                        le16Count++;
                    }
                }
                else if (b1 == 0x0 && ((b2 >= 0x20 && b2 <= 0x7E) || b2 == 0x0A || b2 == 0x0D))
                {
                    // check for UTF-32
                    if (i > 2 && buff[i - 1] == 0x0 && buff[i - 2] == 0x0)
                    {
                        be32Count++;
                    }
                    else
                    {
                        be16Count++;
                    }
                }

                if (le16Count > 2)
                {
                    encoding = Encoding.Unicode;
                    break;
                }
                else if (le32Count > 2)
                {
                    encoding = Encoding.UTF32;
                    break;
                }
                else if (be16Count > 2)
                {
                    encoding = Encoding.BigEndianUnicode;
                    break;
                }
                else if (be32Count > 2)
                {
                    encoding = Encoding.GetEncoding(12001);
                    break;
                }
            }

            return encoding;
        }

        /// <summary>
        /// Returns true is file is binary.
        /// </summary>
        /// <param name="filePath">Path to a file</param>
        /// <returns>True is file is binary otherwise false</returns>
        public static bool IsBinary(string srcFile)
        {
            try
            {
                if (File.Exists(srcFile))
                {
                    using FileStream readStream = File.Open(srcFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    return IsBinary(readStream);
                }
            }
            catch
            {
                // ignore - file cannot be opened
            }
            return false;
        }

        public static bool IsBinary(Stream stream)
        {
            bool result = false;
            try
            {
                byte[] buffer = new byte[1024];
                int count = stream.Read(buffer, 0, buffer.Length);
                for (int i = 0; i < count - 3; i++)
                {
                    // check for 4 consecutive nulls - 2 will give false positive on UTF-32 and some UTF-16
                    if (buffer[i] == 0 && buffer[i + 1] == 0 && buffer[i + 2] == 0 && buffer[i + 3] == 0)
                    {
                        result = true;
                        break;
                    }
                }
            }
            catch
            {
                result = false;
            }
            finally
            {
                // reset the stream back to the beginning
                stream.Seek(0, SeekOrigin.Begin);
            }
            return result;
        }

        public static bool IsRTL(string srcFile, Encoding encoding)
        {
            if (File.Exists(srcFile))
            {
                using FileStream readStream = File.Open(srcFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return IsRTL(readStream, encoding);
            }
            return false;
        }

        public static bool IsRTL(Stream stream, Encoding encoding)
        {
            using StreamReader streamReader = new(stream, encoding);
            string? line = streamReader.ReadLine();
            if (!string.IsNullOrEmpty(line))
            {
                return IsRTL(line);
            }
            return false;
        }

        public static bool IsRTL(string text)
        {
            bool isRtl = false;
            if (!string.IsNullOrWhiteSpace(text))
            {
                isRtl = RtlRegex().IsMatch(text);
            }
            return isRtl;
        }


        /// <summary>
        /// Gets the set of all extensions (lowercase and including the period) handled by a plugin
        /// </summary>
        public static HashSet<string> AllPluginExtensions { get; } = [];

        /// <summary>
        /// Returns true if the file is a file type handled by a plugin - probably binary 
        /// so no need to test for binary or encoding
        /// </summary>
        /// <param name="srcFile"></param>
        /// <returns></returns>
        public static bool IsPluginFile(string srcFile)
        {
            string ext = Path.GetExtension(srcFile).ToLower();
            return AllPluginExtensions.Contains(ext);
        }

        public static bool HasPluginExtension(params string[] filters)
        {
            // first check for 'any file, any file type' filters:
            if (filters.Length == 1 && string.IsNullOrEmpty(filters[0]))
            {
                return true;
            }

            foreach (string filter in filters)
            {
                if (filter == "*" || filter == "*.*" || filter.EndsWith(".*", StringComparison.Ordinal))
                    return true;
            }

            foreach (var ext in AllPluginExtensions)
            {
                foreach (string filter in filters)
                {
                    if (filter.Contains(ext, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Returns true if the source file extension is a recognized archive file
        /// </summary>
        /// <param name="srcFile">a file name</param>
        /// <returns></returns>
        public static bool IsArchive(string srcFile)
        {
            if (!string.IsNullOrWhiteSpace(srcFile))
            {
                return IsArchiveExtension(Path.GetExtension(srcFile));
            }
            return false;
        }

        public static bool IsFileInArchive(string srcFile)
        {
            return srcFile.Contains(ArchiveDirectory.ArchiveSeparator, StringComparison.Ordinal);
        }

        /// <summary>
        /// Returns true if the parameter is a recognized archive file format file extension.
        /// </summary>
        /// <param name="ext">a file extension, with/without a leading '.'</param>
        /// <returns></returns>
        public static bool IsArchiveExtension(string ext)
        {
            if (!string.IsNullOrWhiteSpace(ext))
            {
                // regex extensions may have a 'match end of line' char: remove it
                ext = ext.TrimStart('.').TrimEnd('$').ToLower(CultureInfo.CurrentCulture);
                return ArchiveExtensions.Contains(ext);
            }
            return false;
        }

        /// <summary>
        /// Gets or set the list of archive extensions (lowercase, without leading '.')
        /// </summary>
        public static List<string> ArchiveExtensions => ArchiveDirectory.Extensions;

        /// <summary>
        /// Add DirectorySeparatorChar to the end of the folder path if does not exist
        /// </summary>
        /// <param name="name">Folder path</param>
        /// <returns></returns>
        public static string FixFolderName(string name)
        {
            if (name != null && name.Length > 1 && name[^1] != Path.DirectorySeparatorChar)
                name += Path.DirectorySeparatorChar;
            return name ?? string.Empty;
        }

        /// <summary>
        /// Validates whether the path is a valid directory, file, or list of files
        /// </summary>
        /// <param name="path">Path to one or many files separated by semi-colon or path to a folder</param>
        /// <returns>True is all paths are valid, otherwise false</returns>
        public static bool IsPathValid(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                    return false;

                string[] paths = UiUtils.SplitPath(path, false);
                foreach (string subPath in paths)
                {
                    if (!File.Exists(subPath) && !Directory.Exists(subPath))
                        return false;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool PrepareSearchPatterns(FileFilter filter, List<string> includeSearchPatterns)
        {
            bool handled = false;
            if (!filter.IsRegex && !filter.NamePatternToInclude.Contains("#!", StringComparison.Ordinal))
            {
                var includePatterns = UiUtils.SplitPattern(filter.NamePatternToInclude, false);
                foreach (var pattern in includePatterns)
                {
                    if (pattern == "*.doc" || pattern == "*.xls" || pattern == "*.ppt")
                        includeSearchPatterns.Add(pattern + "*");
                    else
                        includeSearchPatterns.Add(pattern);
                }
                handled = true;
            }
            return handled;
        }

        public static void PrepareFilters(FileFilter filter,
            List<Regex> includeRegexPatterns, List<Regex> excludeRegexPatterns,
            List<Regex> includeShebangPatterns, bool includePatternHandled)
        {
            if (includeRegexPatterns == null || excludeRegexPatterns == null || includeShebangPatterns == null)
                return;

            var includePatterns = UiUtils.SplitPattern(filter.NamePatternToInclude, filter.IsRegex);
            if (HasShebangPattern(includePatterns))
            {
                foreach (var pattern in includePatterns.Where(p => HasShebangPattern(p)))
                {
                    includeShebangPatterns.Add(GetRegex(pattern, filter.IsRegex));
                }
            }

            // non-regex include patterns are used as search patterns in the call to EnumerateFiles
            if (filter.IsRegex || !includePatternHandled)
            {
                foreach (var pattern in includePatterns.Where(p => !HasShebangPattern(p)))
                {
                    includeRegexPatterns.Add(GetRegex(pattern, filter.IsRegex));
                }
            }

            var excludePatterns = UiUtils.SplitPattern(filter.NamePatternToExclude, filter.IsRegex);
            foreach (var pattern in excludePatterns)
            {
                excludeRegexPatterns.Add(GetRegex(pattern, filter.IsRegex));
            }

            if (!string.IsNullOrEmpty(filter.IgnoreFilterFile))
            {
                FillIgnorePatterns(filter.IgnoreFilterFile, excludeRegexPatterns, null);
            }
        }

        private static void FillIgnorePatterns(string filePath, List<Regex> patternList, List<string>? rawPatterns)
        {
            if (File.Exists(filePath))
            {
                // exclude the dngrep.ignore file
                var fileName = Path.GetFileName(filePath);
                if (fileName.Equals("dngrep.ignore", StringComparison.OrdinalIgnoreCase))
                {
                    var regex = GetRegex(fileName, false);
                    if (!patternList.Contains(regex))
                    {
                        patternList.Add(GetRegex(fileName, false));
                        rawPatterns?.Add(fileName);
                    }
                }

                FileSearchType mode = FileSearchType.Asterisk;
                foreach (string line in File.ReadAllLines(filePath, Encoding.UTF8))
                {
                    if (line.StartsWith('#'))
                    {
                        continue;
                    }

                    Match patternType = PatternTypeRegex().Match(line);
                    if (patternType.Success)
                    {
                        if (patternType.Groups.Count > 1)
                        {
                            Group group = patternType.Groups[1];
                            string name = group.Value;
                            if (name.Equals("regex", StringComparison.OrdinalIgnoreCase) ||
                                name.Equals("regular expression", StringComparison.OrdinalIgnoreCase))
                            {
                                mode = FileSearchType.Regex;
                            }
                            else // wildcard, asterisk, or whatever defaul to this
                            {
                                mode = FileSearchType.Asterisk;
                            }
                        }
                        continue;
                    }

                    // a pattern line
                    string pattern = line;
                    int pos = pattern.IndexOf('#', 0);
                    if (pos > 0)
                    {
                        pattern = pattern[..pos].Trim();
                    }
                    if (!string.IsNullOrWhiteSpace(pattern))
                    {
                        var regexPatten = GetRegex(pattern, mode == FileSearchType.Regex);
                        if (!patternList.Contains(regexPatten))
                        {
                            patternList.Add(regexPatten);
                            rawPatterns?.Add(pattern);
                        }
                    }
                }
            }
        }

        public static List<(string path, string pattern)> GetCompositeIgnoreList(string fileOrFolderPath,
            string filePatternIgnore, bool isRegex, string ignoreFilePath)
        {
            List<(string path, string pattern)> results = [];
            foreach (var subPath in UiUtils.SplitPath(fileOrFolderPath, false))
            {
                List<Regex> regexList = [];
                List<string> patterns = [];
                if (!string.IsNullOrWhiteSpace(filePatternIgnore))
                {
                    var excludePatterns = UiUtils.SplitPattern(filePatternIgnore, isRegex);
                    foreach (var pattern in excludePatterns)
                    {
                        Regex regex = GetRegex(pattern, isRegex);
                        regexList.Add(regex);
                        patterns.Add(pattern);
                    }
                }

                if (!string.IsNullOrEmpty(ignoreFilePath))
                {
                    FillIgnorePatterns(ignoreFilePath, regexList, patterns);
                }

                string dnGrepIgnore = Path.Combine(subPath, "dngrep.ignore");
                if (File.Exists(dnGrepIgnore))
                {
                    FillIgnorePatterns(dnGrepIgnore, regexList, patterns);
                }

                results.Add(new(subPath, string.Join(";", patterns)));
            }
            return results;
        }

        private static Regex GetRegex(string pattern, bool isRegex)
        {
            lock (regexLock)
            {
                try
                {
                    if (!isRegex)
                        pattern = WildcardToRegex(pattern);

                    if (pattern.Equals(NoExtensionPattern, StringComparison.Ordinal))
                        return NoExtensionRegex();

                    if (pattern.Equals(DotFilesPattern, StringComparison.Ordinal))
                        return DotFilesRegex();

                    if (!regexCache.TryGetValue(pattern, out Regex? regex))
                    {
                        regex = new Regex(pattern, RegexOptions.IgnoreCase);
                        regexCache.Add(pattern, regex);
                    }

                    return regex;
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "Failed in Utils.GetRegex");
                    throw;
                }
            }
        }

        public static IEnumerable<FileData> GetFileListIncludingArchives(FileFilter filter,
            PauseCancelToken pauseCancelToken = default)
        {
            foreach (var file in GetFileListEx(filter, pauseCancelToken))
            {
                if (IsArchive(file))
                {
                    foreach (var innerFile in ArchiveDirectory.EnumerateFiles(file, filter, pauseCancelToken))
                    {
                        yield return innerFile;
                    }
                }
                else
                {
                    yield return new FileData(file);
                }
            }
        }

        /// <summary>
        /// Iterator based file search
        /// Searches folder and it's subfolders for files that match pattern and
        /// returns array of strings that contain full paths to the files.
        /// If no files found returns 0 length array.
        /// </summary>
        /// <param name="filter">the file filter parameters</param>
        /// <returns></returns>
        public static IEnumerable<string> GetFileListEx(FileFilter filter,
            PauseCancelToken pauseCancelToken = default)
        {
            if (string.IsNullOrWhiteSpace(filter.Path) || filter.NamePatternToInclude == null)
            {
                yield break;
            }

#pragma warning disable CA1868
            // Hash set to ensure file name uniqueness
            HashSet<string> matches = [];

            if (filter.UseEverything)
            {
                var files = GetFileListEverything(filter);
                foreach (var file in files)
                {
                    if (!matches.Contains(file))
                    {
                        matches.Add(file);
                        yield return file;
                    }
                }
                yield break;
            }
#pragma warning restore CA1868

            List<string> includeSearchPatterns = [];
            bool hasSearchPattern = PrepareSearchPatterns(filter, includeSearchPatterns);

            List<Regex> includeRegexPatterns = [];
            List<Regex> excludeRegexPatterns = [];
            List<Regex> includeShebangPatterns = [];
            PrepareFilters(filter, includeRegexPatterns, excludeRegexPatterns, includeShebangPatterns, hasSearchPattern);

            foreach (var subPath in UiUtils.SplitPath(filter.Path, false))
            {
                List<Regex> mergedExcludePatterns = excludeRegexPatterns;
                string dnGrepIgnore = Path.Combine(subPath, "dngrep.ignore");
                if (File.Exists(dnGrepIgnore))
                {
                    // make a copy so we don't append to the original list
                    // if there are multiple root directories, the file only applies to the current directory
                    mergedExcludePatterns = new(excludeRegexPatterns);
                    FillIgnorePatterns(dnGrepIgnore, mergedExcludePatterns, null);
                }

                if (File.Exists(subPath))
                {
                    if (IsArchive(subPath) && filter.IncludeArchive)
                    {
                        matches.Add(subPath);
                        yield return subPath;
                    }
                    else if (IncludeFile(subPath, filter, null, includeSearchPatterns,
                        includeRegexPatterns, mergedExcludePatterns, includeShebangPatterns) &&
                        !matches.Contains(subPath))
                    {
                        matches.Add(subPath);
                        yield return subPath;
                    }
                    continue;
                }
                else if (!Directory.Exists(subPath))
                {
                    continue;
                }

                Gitignore? gitignore = null;
                if (filter.UseGitIgnore)
                {
                    List<string> gitDirectories = SafeDirectory.GetGitignoreDirectories(subPath, filter.IncludeSubfolders, filter.FollowSymlinks, pauseCancelToken);
                    if (gitDirectories.Count != 0)
                    {
                        gitignore = GitUtil.GetGitignore(gitDirectories);
                    }
                }

                foreach (var filePath in SafeDirectory.EnumerateFiles(subPath, includeSearchPatterns, mergedExcludePatterns, gitignore, filter, pauseCancelToken))
                {
                    if (IsArchive(filePath))
                    {
                        if (filter.IncludeArchive)
                        {
                            matches.Add(filePath);
                            yield return filePath;
                        }
                    }
                    // EnumerateFiles already applied the exclude patterns, so don't repeat them here
                    else if (IncludeFile(filePath, filter, null, includeSearchPatterns,
                        includeRegexPatterns, [], includeShebangPatterns) &&
                        !matches.Contains(filePath))
                    {
                        matches.Add(filePath);
                        yield return filePath;
                    }
                }
            }
        }

        private static IEnumerable<string> GetFileListEverything(FileFilter filter)
        {
            string searchString = filter.Path.Trim();
            if (filter.IncludeArchive)
            {
                // to search in archives, ask Everything to return all archive files
                searchString += "|*." + string.Join("|*.", ArchiveExtensions);
            }

            if (filter.SizeFrom > 0 || filter.SizeTo > 0)
            {
                searchString = AddEverythingSizeFilters(filter, searchString);
            }

            if (filter.DateFilter != FileDateFilter.None)
            {
                searchString = AddEverythingDateFilters(filter, searchString);
            }

            foreach (var fileInfo in EverythingSearch.FindFiles(searchString, filter.IncludeHidden))
            {
                FileData fileData = new(fileInfo);

                if (IsArchive(fileInfo.FullName))
                {
                    if (filter.IncludeArchive)
                    {
                        yield return fileInfo.FullName;
                    }
                    else
                    {
                        continue;
                    }
                }
                else if (IncludeFile(fileInfo.FullName, filter, fileData, null,
                    null, null, null))
                {
                    yield return fileInfo.FullName;
                }
            }
        }

        private static string AddEverythingSizeFilters(FileFilter filter, string searchString)
        {
            if ((filter.SizeFrom > 0 || filter.SizeTo > 0) && !searchString.Contains("size:", StringComparison.Ordinal))
            {
                if (filter.SizeFrom == 0)
                {
                    searchString += $" size:<={filter.SizeTo}kb";
                }
                else if (filter.SizeTo == 0)
                {
                    searchString += $" size:>={filter.SizeFrom}kb";
                }
                else
                {
                    searchString += $" size:{filter.SizeFrom}kb-{filter.SizeTo}kb";
                }
            }
            return searchString;
        }

        private static string AddEverythingDateFilters(FileFilter filter, string searchString)
        {
            if (!filter.StartTime.HasValue && !filter.EndTime.HasValue)
            {
                return searchString;
            }

            string function = string.Empty;
            if (filter.DateFilter == FileDateFilter.Modified)
            {
                if (!searchString.Contains("datemodified:", StringComparison.Ordinal) && !searchString.Contains("dm:", StringComparison.Ordinal))
                {
                    function += " dm:";
                }
            }
            else if (filter.DateFilter == FileDateFilter.Created)
            {
                if (!searchString.Contains("datecreated:", StringComparison.Ordinal) && !searchString.Contains("dc:", StringComparison.Ordinal))
                {
                    function += " dc:";
                }
            }

            if (!string.IsNullOrEmpty(function))
            {
                if (filter.StartTime.HasValue && filter.EndTime.HasValue)
                {
                    function += $"{filter.StartTime.Value.ToIso8601DateTime()}-{filter.EndTime.Value.ToIso8601DateTime()}";
                }
                else if (filter.StartTime.HasValue)
                {
                    function += $">={filter.StartTime.Value.ToIso8601DateTime()}";
                }
                else if (filter.EndTime.HasValue)
                {
                    function += $"<={filter.EndTime.Value.ToIso8601DateTime()}";
                }
            }

            return searchString + function;
        }

        /// <summary>
        /// Evaluates if a file should be included in the search results
        /// </summary>
        public static bool IncludeFile(string filePath, FileFilter filter, FileData? fileInfo,
            IList<string>? includeSearchPatterns,
            IList<Regex>? includeRegexPatterns, IList<Regex>? excludeRegexPatterns,
            IList<Regex>? includeShebangPatterns)
        {
            try
            {
                // check filters that do not read the file first...

                // regex include
                if (includeRegexPatterns != null && includeRegexPatterns.Count > 0)
                {
                    bool include = false;
                    foreach (var pattern in includeRegexPatterns)
                    {
                        if (pattern.IsMatch(filePath))
                        {
                            include = true;
                            break;
                        }
                    }
                    if (!include)
                    {
                        return false;
                    }
                }

                // exclude this file?
                // wildcard exclude files are converted to regex
                if (excludeRegexPatterns != null && excludeRegexPatterns.Count > 0)
                {
                    foreach (var pattern in excludeRegexPatterns)
                    {
                        if (pattern.IsMatch(filePath))
                        {
                            return false;
                        }
                    }
                }

                if (filter.SkipRemoteCloudStorageFiles)
                {
                    var attr = (uint)File.GetAttributes(filePath);
                    bool FILE_ATTRIBUTE_RECALL_ON_OPEN = (attr & 0x40000) == 0x40000;
                    bool FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = (attr & 0x400000) == 0x400000;

                    if (FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS || FILE_ATTRIBUTE_RECALL_ON_OPEN)
                    {
                        return false;
                    }
                }

                if ((filter.SizeFrom > 0 || filter.SizeTo > 0) && !filter.UseEverything) // Everything search has size filter in query
                {
                    fileInfo ??= new FileData(filePath);

                    long sizeKB = fileInfo.Length / 1000;
                    if (filter.SizeFrom > 0 && sizeKB < filter.SizeFrom)
                    {
                        return false;
                    }
                    if (filter.SizeTo > 0 && sizeKB > filter.SizeTo)
                    {
                        return false;
                    }
                }

                if (filter.DateFilter != FileDateFilter.None && !filter.UseEverything) // Everything search has date filter in query
                {
                    fileInfo ??= new FileData(filePath);

                    DateTime fileDate = filter.DateFilter == FileDateFilter.Created ? fileInfo.CreationTime : fileInfo.LastWriteTime;
                    if (filter.StartTime.HasValue && fileDate < filter.StartTime.Value)
                    {
                        return false;
                    }
                    if (filter.EndTime.HasValue && fileDate >= filter.EndTime.Value)
                    {
                        return false;
                    }
                }

                if (!filter.IncludeBinary && !IsArchive(filePath) && !IsFileInArchive(filePath))
                {
                    //bool isExcelMatch = IsExcelFile(filePath) && (includeSearchPatterns?.Contains(".xls", StringComparison.OrdinalIgnoreCase) ?? false);
                    //bool isWordMatch = IsWordFile(filePath) && (includeSearchPatterns?.Contains(".doc", StringComparison.OrdinalIgnoreCase) ?? false);
                    //bool isPowerPointMatch = IsPowerPointFile(filePath) && (includeSearchPatterns?.Contains(".ppt", StringComparison.OrdinalIgnoreCase) ?? false);
                    //bool isPdfMatch = IsPdfFile(filePath) && (includeSearchPatterns?.Contains(".pdf", StringComparison.OrdinalIgnoreCase) ?? false);

                    //// When searching for Excel, Word, PowerPoint, or PDF files, skip the binary file check:
                    //// If someone is searching for one of these types, don't make them include binary to 
                    //// find their files.

                    // do not test files handled by a plugin for binary
                    if (!IsPluginFile(filePath) && IsBinary(filePath))
                    {
                        return false;
                    }
                }

                if (includeShebangPatterns != null && includeShebangPatterns.Any())
                {
                    bool include = false;
                    foreach (var pattern in includeShebangPatterns)
                    {
                        if (CheckShebang(filePath, pattern.ToString()))
                        {
                            include = true;
                            break;
                        }
                    }
                    if (!include)
                    {
                        return false;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Failure in applying file filters");
                // returning true shows an error in the results tree
                return true;
            }
        }

        public static bool HasShebangPattern(IList<string> patterns)
        {
            if (patterns != null)
            {
                foreach (var pattern in patterns)
                {
                    if (HasShebangPattern(pattern))
                        return true;
                }
            }
            return false;
        }

        public static bool HasShebangPattern(string pattern)
        {
            return pattern != null && pattern.Length > 2 && pattern[0] == '#' && pattern[1] == '!';
        }

        public static bool CheckShebang(string file, string pattern)
        {
            if (HasShebangPattern(pattern))
            {
                using FileStream readStream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return CheckShebang(readStream, pattern);
            }
            return false;
        }

        public static bool CheckShebang(Stream stream, string pattern)
        {
            bool result = false;
            if (HasShebangPattern(pattern))
            {
                using (StreamReader streamReader = new(stream, GetFileEncoding(stream), false, 4096, true))
                {
                    string? firstLine = streamReader.ReadLine();
                    // Check if first 2 bytes are '#!'
                    if (!string.IsNullOrEmpty(firstLine) && firstLine.Length > 1 &&
                        firstLine[0] == '#' && firstLine[1] == '!')
                    {
                        // Do more reading (start from 3rd character in case there is a space after #!)
                        for (int i = 3; i < firstLine.Length; i++)
                        {
                            if (firstLine[i] == ' ' || firstLine[i] == '\r' || firstLine[i] == '\n' || firstLine[i] == '\t')
                            {
                                firstLine = firstLine[..i];
                                break;
                            }
                        }
                        result = Regex.IsMatch(firstLine[2..].Trim(), pattern[2..], RegexOptions.IgnoreCase);
                    }
                }
                stream.Seek(0, SeekOrigin.Begin);
            }
            return result;
        }

        /// <summary>
        /// Searches folder and it's subfolders for files that match pattern and
        /// returns array of strings that contain full paths to the files.
        /// If no files found returns 0 length array.
        /// </summary>
        /// <param name="path">Path to one or many files separated by semi-colon or path to a folder</param>
        /// <param name="namePatternToInclude">File name pattern. (E.g. *.cs) or regex to include. If null returns empty array. If empty string returns all files.</param>
        /// <param name="namePatternToExclude">File name pattern. (E.g. *.cs) or regex to exclude. If null or empty is ignored.</param>
        /// <param name="isRegex">Whether to use regex as search pattern. Otherwise use asterisks</param>
        /// <param name="useEverything">use Everything for file search</param>
        /// <param name="includeSubfolders">Include sub-folders</param>
        /// <param name="includeHidden">Include hidden folders</param>
        /// <param name="includeBinary">Include binary files</param>
        /// <param name="includeArchive">Include search in archives</param>
        /// <param name="followSymlinks">Include search in symbolic links</param>
        /// <param name="sizeFrom">Size in KB</param>
        /// <param name="sizeTo">Size in KB</param>
        /// <param name="dateFilter">Filter by file modified or created date time range</param>
        /// <param name="startTime">start of time range</param>
        /// <param name="endTime">end of time range</param>
        /// <param name="useGitignore">use .gitignore as an exclusion filter</param>
        /// <param name="maxSubfolderDepth">Max depth of sub-folders where 0 is root only and -1 is all</param>
        /// <returns>List of file or empty list if nothing is found</returns>
        public static string[] GetFileList(string path, string namePatternToInclude, string namePatternToExclude, bool isRegex,
            bool useEverything, bool includeSubfolders, bool includeHidden, bool includeBinary, bool includeArchive,
            bool followSymlinks, int sizeFrom, int sizeTo, FileDateFilter dateFilter,
            DateTime? startTime, DateTime? endTime, bool useGitignore, int maxSubfolderDepth,
            bool skipRemoteCloudStorageFiles = true, string ignoreFilterFile = "", PauseCancelToken pauseCancelToken = default)
        {
            var filter = new FileFilter(path, namePatternToInclude, namePatternToExclude, isRegex,
                useGitignore, useEverything, includeSubfolders, maxSubfolderDepth, includeHidden, includeBinary,
                includeArchive, followSymlinks, sizeFrom, sizeTo, dateFilter, startTime, endTime,
                skipRemoteCloudStorageFiles, ignoreFilterFile);
            return GetFileListEx(filter, pauseCancelToken).ToArray();
        }

        /// <summary>
        /// Converts windows asterisk based file pattern to regex
        /// </summary>
        /// <param name="wildcard">Asterisk based pattern</param>
        /// <returns>Regular expression of null is empty</returns>
        public static string WildcardToRegex(string wildcard)
        {
            if (string.IsNullOrWhiteSpace(wildcard)) return wildcard;

            // special meaning files with no extension
            if (wildcard.Equals("*.", StringComparison.OrdinalIgnoreCase))
                return NoExtensionPattern;

            // special meaning files that start with dot
            if (wildcard.Equals(".*", StringComparison.OrdinalIgnoreCase))
                return DotFilesPattern;

            StringBuilder sb = new();

            char[] chars = wildcard.ToCharArray();
            for (int i = 0; i < chars.Length; ++i)
            {
                if (chars[i] == '*')
                    sb.Append(".*");
                else if (chars[i] == '?')
                    sb.Append('.');
                else if (metacharacters.Contains(chars[i], StringComparison.Ordinal))
                    sb.Append('\\').Append(chars[i]); // prefix all metacharacters with backslash
                else
                    sb.Append(chars[i]);
            }
            sb.Append('$');
            return sb.ToString().ToLowerInvariant();
        }

        /// <summary>
        /// Open file using either default editor or the one provided via customEditor parameter
        /// </summary>
        /// <param name="fileName">File to open</param>
        /// <param name="line">Line number</param>
        /// <param name="useCustomEditor">True if customEditor parameter is provided</param>
        /// <param name="customEditor">Custom editor path</param>
        /// <param name="customEditorArgs">Arguments for custom editor</param>
        public static void OpenFile(OpenFileArgs args)
        {
            string filePath = args.SearchResult.FileNameDisplayed;
            if (filePath != null && filePath.Length > 260)
            {
                filePath = PathEx.GetShort83Path(filePath);
            }

            if (!string.IsNullOrEmpty(filePath))
            {
                if (!args.UseCustomEditor || string.IsNullOrWhiteSpace(args.CustomEditorName))
                {
                    try
                    {
                        ProcessStartInfo startInfo = new()
                        {
                            FileName = UiUtils.Quote(filePath),
                            UseShellExecute = true,
                        };
                        try
                        {
                            using var proc = Process.Start(startInfo);
                        }
                        catch (Win32Exception ex)
                        {
                            if (ex.NativeErrorCode == ErrorRequiresElevation)
                            {
                                startInfo.Verb = "runas";
                                startInfo.UseShellExecute = true;

                                using var proc = Process.Start(startInfo);
                            }
                            else
                            {
                                throw;
                            }
                        }
                    }
                    catch
                    {
                        ProcessStartInfo startInfo = new("notepad.exe")
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            Arguments = filePath
                        };
                        try
                        {
                            using var proc = Process.Start(startInfo);
                        }
                        catch (Win32Exception ex)
                        {
                            if (ex.NativeErrorCode == ErrorRequiresElevation)
                            {
                                startInfo.Verb = "runas";
                                startInfo.UseShellExecute = true;

                                using var proc = Process.Start(startInfo);
                            }
                            else
                            {
                                throw;
                            }
                        }
                    }
                }
                else
                {
                    var editorList = GrepSettings.Instance.Get<List<CustomEditor>>(GrepSettings.Key.CustomEditors);
                    CustomEditor? editor = null;
                    if (args.CustomEditorName.Equals(OpenFileArgs.DefaultEditor, StringComparison.Ordinal))
                    {
                        string fileType = Path.GetExtension(filePath).TrimStart('.');

                        editor = editorList.FirstOrDefault(r => r.ExtensionList
                            .Contains(fileType, StringComparison.OrdinalIgnoreCase));

                        if (editor == null)
                        {
                            editor = editorList.FirstOrDefault();
                        }
                    }
                    else
                    {
                        editor = editorList.FirstOrDefault(e => e.Label
                            .Equals(args.CustomEditorName, StringComparison.OrdinalIgnoreCase));
                    }

                    if (editor != null)
                    {
                        int pageNumber = args.PageNumber > 0 ? args.PageNumber : 1;

                        bool escapeQuotes = editor.EscapeQuotes;
                        string matchValue = escapeQuotes ? EscapeQuotes(args.FirstMatch) : args.FirstMatch;

                        ProcessStartInfo startInfo = new(editor.Path)
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            Arguments = editor.Args.Replace("%file", UiUtils.Quote(filePath), StringComparison.Ordinal)
                                .Replace("%page", pageNumber.ToString(), StringComparison.Ordinal)
                                .Replace("%line", args.LineNumber.ToString(), StringComparison.Ordinal)
                                .Replace("%pattern", args.Pattern, StringComparison.Ordinal)
                                .Replace("%match", matchValue, StringComparison.Ordinal)
                                .Replace("%column", args.ColumnNumber.ToString(), StringComparison.Ordinal),
                        };
                        try
                        {
                            using var proc = Process.Start(startInfo);
                        }
                        catch (Win32Exception ex)
                        {
                            if (ex.NativeErrorCode == ErrorRequiresElevation)
                            {
                                startInfo.Verb = "runas";
                                startInfo.UseShellExecute = true;

                                using var proc = Process.Start(startInfo);
                            }
                            else
                            {
                                throw;
                            }
                        }
                    }
                }
            }
        }

        private static string EscapeQuotes(string value)
        {
            return value.Replace("\"", "\\\"", StringComparison.Ordinal);
        }

        /// <summary>
        /// Returns path to a temp folder used by dnGREP (including trailing slash). If folder does not exist
        /// it gets created.
        /// </summary>
        /// <returns></returns>
        public static string GetTempFolder()
        {
            string tempPath = Path.Combine(Path.GetTempPath(), tempFolderName);
            if (!Directory.Exists(tempPath))
            {
                Directory.CreateDirectory(tempPath);
            }
            return tempPath + Path.DirectorySeparatorChar;
        }

        /// <summary>
        /// Returns a path to a folder used by plugins to extract files to text. The location of this
        /// folder depends on user settings, and may be the temp folder <see cref="GetTempFolder"/>
        /// </summary>
        /// <returns></returns>
        public static string GetCacheFolder()
        {
            if (GrepSettings.Instance.Get<bool>(GrepSettings.Key.CacheExtractedFiles))
            {
                string userCachePath = GrepSettings.Instance.Get<string>(GrepSettings.Key.CacheFilePath);
                string cachePath = !IsValidPath(userCachePath) ||
                    GrepSettings.Instance.Get<bool>(GrepSettings.Key.CacheFilesInTempFolder) ?
                    Path.Combine(Path.GetTempPath(), defaultCacheFolderName) :
                    userCachePath;

                if (!Directory.Exists(cachePath))
                {
                    Directory.CreateDirectory(cachePath);
                }
                return cachePath + Path.DirectorySeparatorChar;
            }
            else
            {
                return GetTempFolder();
            }
        }

        /// <summary>
        /// Deletes temp folder
        /// </summary>
        public static void DeleteTempFolder()
        {
            string tempPath = Path.Combine(Path.GetTempPath(), tempFolderName);
            try
            {
                if (Directory.Exists(tempPath))
                    DeleteFolder(tempPath);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Failed to delete temp folder");
            }
        }

        public static void CleanCacheFiles()
        {
            Stopwatch sw = Stopwatch.StartNew();
            int days = GrepSettings.Instance.Get<int>(GrepSettings.Key.CacheFilesCleanDays);
            if (days > 0 &&
                GrepSettings.Instance.Get<bool>(GrepSettings.Key.CacheExtractedFiles))
            {
                string userCachePath = GrepSettings.Instance.Get<string>(GrepSettings.Key.CacheFilePath);
                string cachePath = !IsValidPath(userCachePath) ||
                    GrepSettings.Instance.Get<bool>(GrepSettings.Key.CacheFilesInTempFolder) ?
                    Path.Combine(Path.GetTempPath(), defaultCacheFolderName) :
                    userCachePath;

                int count = 0;
                if (Directory.Exists(cachePath))
                {
                    DateTime expiredDate = DateTime.Now.AddDays(-1 * days);

                    foreach (string file in Directory.GetFiles(cachePath, "*.*", SearchOption.AllDirectories))
                    {
                        FileInfo fileInfo = new(file);
                        if (fileInfo.LastAccessTime < expiredDate)
                        {
                            try
                            {
                                DeleteFile(file);
                                count++;
                            }
                            catch (Exception ex)
                            {
                                logger.Error(ex, $"Failed to delete file from cache folder '{file}'");
                            }
                        }
                    }
                }
                logger.Info($"Deleted {count} files from cache in {sw.ElapsedMilliseconds} ms");
            }
        }

        /// <summary>
        /// Returns path to a folder used by dnGREP for undo files (including trailing slash). If folder does not exist
        /// it gets created.
        /// </summary>
        /// <returns></returns>
        public static string GetUndoFolder()
        {
            string undoPath = Path.Combine(Path.GetTempPath(), undoFolderName);
            if (!Directory.Exists(undoPath))
            {
                Directory.CreateDirectory(undoPath);
            }
            return undoPath + Path.DirectorySeparatorChar;
        }

        /// <summary>
        /// Deletes undo folder
        /// </summary>
        public static void DeleteUndoFolder()
        {
            string undoPath = Path.Combine(Path.GetTempPath(), undoFolderName);
            try
            {
                if (Directory.Exists(undoPath))
                    DeleteFolder(undoPath);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Failed to delete undo folder");
            }
        }

        public static string GetUniqueKey(int size)
        {
            var data = RandomNumberGenerator.GetBytes(size);
            StringBuilder result = new(size);
            for (int i = 0; i < size; i++)
            {
                var idx = data[i] % chars.Length;
                result.Append(chars[idx]);
            }

            return result.ToString();
        }

        /// <summary>
        /// Open folder in explorer
        /// </summary>
        /// <param name="fileName"></param>
        public static void OpenContainingFolder(string fileName)
        {
            if (fileName.Length > 260)
                fileName = PathEx.GetShort83Path(fileName);

            ProcessStartInfo startInfo = new("explorer.exe", "/select,\"" + fileName + "\"");
            try
            {
                using var proc = Process.Start(startInfo);
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode == ErrorRequiresElevation)
                {
                    startInfo.Verb = "runas";
                    startInfo.UseShellExecute = true;

                    using var proc = Process.Start(startInfo);
                }
                else
                {
                    throw;
                }
            }
        }

        public static void CompareFiles(IList<string> paths)
        {
            if (paths.Count < 2 || paths.Count > 3)
                throw new ArgumentException("CompareFiles takes 2 or 3 files");

            var settings = GrepSettings.Instance;
            string? application = settings.Get<string>(GrepSettings.Key.CompareApplication);
            string? args = settings.Get<string>(GrepSettings.Key.CompareApplicationArgs);

            if (!string.IsNullOrWhiteSpace(application))
            {
                string appArgs = string.IsNullOrWhiteSpace(args) ? string.Empty : args + " ";
                string fileArgs = string.Join(" ", paths.Select(p => UiUtils.Quote(p)));

                ProcessStartInfo startInfo = new(application)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    Arguments = appArgs + fileArgs
                };
                try
                {
                    using var proc = Process.Start(startInfo);
                }
                catch (Win32Exception ex)
                {
                    if (ex.NativeErrorCode == ErrorRequiresElevation)
                    {
                        startInfo.Verb = "runas";
                        startInfo.UseShellExecute = true;

                        using var proc = Process.Start(startInfo);
                    }
                    else
                    {
                        throw;
                    }
                }
            }
        }

        public static void CompareFiles(IList<GrepSearchResult> files)
        {
            List<string> paths = [];
            foreach (var item in files)
            {
                string filePath = item.FileNameReal;
                if (IsArchive(filePath))
                    filePath = ArchiveDirectory.ExtractToTempFile(item);

                if (!paths.Contains(filePath))
                    paths.Add(filePath);

                if (paths.Count == 3)
                    break;
            }
            CompareFiles(paths);
        }

        /// <summary>
        /// Returns current path of DLL without trailing slash
        /// </summary>
        /// <returns></returns>
        public static string GetCurrentPath()
        {
            return GetCurrentPath(typeof(Utils));
        }

        public static bool IsSubDirectoryOf(this string candidate, string other)
        {
            var isChild = false;
            try
            {
                var candidateInfo = new DirectoryInfo(candidate);
                var otherInfo = new DirectoryInfo(other);

                while (candidateInfo.Parent != null)
                {
                    if (candidateInfo.Parent.FullName == otherInfo.FullName)
                    {
                        isChild = true;
                        break;
                    }
                    else candidateInfo = candidateInfo.Parent;
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, $"Unable to check directories {candidate} and {other}");
            }

            return isChild;
        }


        /// <summary>
        /// Returns current path of DLL without trailing slash
        /// </summary>
        /// <param name="type">Type to check</param>
        /// <returns></returns>
        public static string GetCurrentPath(Type type)
        {
            Assembly? assembly = Assembly.GetAssembly(type);
            return Path.GetDirectoryName(assembly?.Location) ?? string.Empty;
        }

        /// <summary>
        /// Returns read-only from the results
        /// </summary>
        /// <param name="results"></param>
        /// <returns></returns>
        public static List<string> GetReadOnlyFiles(List<GrepSearchResult>? results)
        {
            List<string> files = [];
            if (results == null || results.Count == 0)
                return files;

            foreach (GrepSearchResult result in results)
            {
                if (!files.Contains(result.FileNameReal))
                {
                    if (IsReadOnly(result))
                    {
                        files.Add(result.FileNameReal);
                    }
                }
            }
            return files;
        }

        public static bool IsReadOnly(GrepSearchResult result)
        {
            if (result.IsHexFile)
            {
                return true;
            }

            if (result.IsReadOnlyFileType)
            {
                return true;
            }

            if (File.Exists(result.FileNameReal))
            {
                return File.GetAttributes(result.FileNameReal).HasFlag(FileAttributes.ReadOnly);
            }

            return false;
        }

        public static bool HasReadOnlyAttributeSet(GrepSearchResult? result)
        {
            if (result != null && File.Exists(result.FileNameReal))
            {
                return File.GetAttributes(result.FileNameReal).HasFlag(FileAttributes.ReadOnly);
            }

            return false;
        }

        public static string GetEOL(string path, Encoding encoding)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                using FileStream reader = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using StreamReader streamReader = new(reader, encoding);
                using EolReader eolReader = new(streamReader);
                string? line = eolReader.ReadLine();
                if (!string.IsNullOrEmpty(line))
                {
                    if (line.EndsWith("\r\n", StringComparison.Ordinal))
                        return "\r\n";
                    else if (line.EndsWith('\n'))
                        return "\n";
                    else if (line.EndsWith('\r'))
                        return "\r";
                }
            }
            return string.Empty;
        }

        /// <summary>
        /// Retrieves lines with context based on matches
        /// </summary>
        /// <param name="body">Text</param>
        /// <param name="bodyMatches">List of matches with positions relative to entire text body</param>
        /// <param name="beforeLines">Context line (before)</param>
        /// <param name="afterLines">Context line (after</param>
        /// <param name="isPdfText">True if file is PDF text and to count a page for each \f character</param>
        /// <returns></returns>
        public static List<GrepLine> GetLinesEx(TextReader body, List<GrepMatch> bodyMatches, int beforeLines, int afterLines, bool isPdfText = false)
        {
            if (body == null || bodyMatches == null)
                return [];

            List<GrepMatch> bodyMatchesClone = CloneAndSplitGroups(bodyMatches);

            Dictionary<int, GrepLine> results = [];
            Dictionary<int, int> lineToPageMap = [];
            List<GrepLine> contextLines = [];
            Dictionary<int, string> lineStrings = [];
            List<int> lineNumbers = [];
            List<GrepMatch> matches = [];

            string ZWSP = char.ConvertFromUtf32(0x200B); //zero width space 

            // Context line (before)
            Queue<string> beforeQueue = new();
            // Context line (after)
            int currentAfterLine = 0;
            bool startRecordingAfterLines = false;
            // Current page (using \f)
            int pageNumber = isPdfText ? 1 : -1;
            // Current line
            int lineNumber = 0;
            // Current index of character
            int currentIndex = 0;
            int startIndex = 0;
            int tempLinesTotalLength = 0;
            int startLine = 0;
            int startOfLineOfStartOfMatch = 0;
            bool startMatched = false;
            Queue<string> lineQueue = new();

            using (EolReader reader = new(body))
            {
                while (!reader.EndOfStream && (bodyMatchesClone.Count > 0 || startRecordingAfterLines))
                {
                    lineNumber++;
                    string? line = reader.ReadLine();
                    if (!string.IsNullOrEmpty(line))
                    {
                        if (isPdfText)
                        {
                            if (reader.EndOfStream && line.Equals("\f", StringComparison.Ordinal))
                            {
                                break;
                            }

                            pageNumber += line.Count(c => c.Equals('\f'));
                            // replace the form feed character with a zero width space; keeps the same character count
                            line = line.Replace("\f", ZWSP, StringComparison.Ordinal);
                            lineToPageMap.Add(lineNumber, pageNumber);
                        }

                        bool moreMatches = true;
                        // Building context queue
                        if (beforeLines > 0)
                        {
                            if (beforeQueue.Count >= beforeLines + 1)
                                beforeQueue.Dequeue();

                            beforeQueue.Enqueue(line.TrimEndOfLine());
                        }
                        if (startRecordingAfterLines && currentAfterLine < afterLines)
                        {
                            currentAfterLine++;
                            contextLines.Add(new GrepLine(lineNumber, line.TrimEndOfLine(), true, null) { PageNumber = pageNumber });
                        }
                        else if (currentAfterLine == afterLines)
                        {
                            currentAfterLine = 0;
                            startRecordingAfterLines = false;
                        }

                        while (moreMatches && bodyMatchesClone.Count > 0)
                        {
                            // Head of match found
                            if (bodyMatchesClone[0].StartLocation >= currentIndex && bodyMatchesClone[0].StartLocation < currentIndex + line.Length && !startMatched)
                            {
                                startMatched = true;
                                moreMatches = true;
                                lineQueue = new Queue<string>();
                                startLine = lineNumber;
                                startOfLineOfStartOfMatch = currentIndex;
                                startIndex = bodyMatchesClone[0].StartLocation - currentIndex;
                                tempLinesTotalLength = 0;

                                // Recording the before match context lines
                                while (beforeQueue.Count > 0)
                                {
                                    // If only 1 line - it is the same as matched line
                                    if (beforeQueue.Count == 1)
                                        beforeQueue.Dequeue();
                                    else
                                        contextLines.Add(new GrepLine(startLine - beforeQueue.Count + 1 + (lineNumber - startLine),
                                            beforeQueue.Dequeue(), true, null)
                                        { PageNumber = pageNumber });
                                }
                            }

                            // Add line to queue
                            if (startMatched)
                            {
                                lineQueue.Enqueue(line);
                                tempLinesTotalLength += line.Length;
                            }

                            // Tail of match found
                            if (bodyMatchesClone[0].StartLocation + bodyMatchesClone[0].Length <= currentIndex + line.Length && startMatched)
                            {
                                startMatched = false;
                                moreMatches = false;
                                int firstLineLength = lineQueue.Peek().Length;
                                bool multilineMatch = startLine != lineNumber;
                                bool multilineGroups = bodyMatchesClone[0].Groups.Any(g => g.StartLocation > firstLineLength);
                                int startOfLineIndex = startOfLineOfStartOfMatch;
                                // Start creating matches
                                for (int i = startLine; i <= lineNumber; i++)
                                {
                                    lineNumbers.Add(i);
                                    string tempLine = lineQueue.Dequeue();
                                    lineStrings[i] = tempLine;

                                    string fileMatchId = bodyMatchesClone[0].FileMatchId;

                                    List<GrepCaptureGroup> lineGroups;
                                    // for multiline regex, get just the groups on the current line
                                    if (multilineMatch)
                                    {
                                        lineGroups = bodyMatchesClone[0].Groups.Where(g => g.StartLocation >= startOfLineIndex &&
                                                g.StartLocation < startOfLineIndex + tempLine.Length &&
                                                g.StartLocation + g.Length <= startOfLineIndex + tempLine.Length)
                                            .Select(g => new GrepCaptureGroup(g.Name, g.StartLocation - startOfLineIndex, g.Length, g.Value, g.FullValue))
                                            .ToList();
                                    }
                                    else if (multilineGroups)
                                    {
                                        lineGroups = bodyMatchesClone[0].Groups.Where(g => g.StartLocation >= currentIndex &&
                                                g.StartLocation < currentIndex + tempLine.Length)
                                            .Select(g => new GrepCaptureGroup(g.Name, g.StartLocation - currentIndex, g.Length, g.Value, g.FullValue))
                                            .ToList();
                                    }
                                    else
                                    {
                                        lineGroups = bodyMatchesClone[0].Groups;
                                    }

                                    // First and only line
                                    if (i == startLine && i == lineNumber)
                                        matches.Add(new GrepMatch(fileMatchId, bodyMatchesClone[0].SearchPattern, i, startIndex, bodyMatchesClone[0].Length, lineGroups, bodyMatchesClone[0].RegexMatchValue));
                                    // First but not last line
                                    else if (i == startLine)
                                        matches.Add(new GrepMatch(fileMatchId, bodyMatchesClone[0].SearchPattern, i, startIndex, tempLine.TrimEndOfLine().Length - startIndex, lineGroups, bodyMatchesClone[0].RegexMatchValue));
                                    // Middle line
                                    else if (i > startLine && i < lineNumber)
                                        matches.Add(new GrepMatch(fileMatchId, bodyMatchesClone[0].SearchPattern, i, 0, tempLine.TrimEndOfLine().Length, lineGroups, bodyMatchesClone[0].RegexMatchValue));
                                    // Last line
                                    else
                                        matches.Add(new GrepMatch(fileMatchId, bodyMatchesClone[0].SearchPattern, i, 0, bodyMatchesClone[0].Length - tempLinesTotalLength + line.Length + startIndex, lineGroups, bodyMatchesClone[0].RegexMatchValue));

                                    startOfLineIndex += tempLine.Length;
                                    startRecordingAfterLines = true;
                                }
                                bodyMatchesClone.RemoveAt(0);
                            }

                            // Another match on this line
                            if (bodyMatchesClone.Count > 0 && bodyMatchesClone[0].StartLocation >= currentIndex && bodyMatchesClone[0].StartLocation < currentIndex + line.Length && !startMatched)
                                moreMatches = true;
                            else
                                moreMatches = false;
                        }

                        currentIndex += line.Length;
                    }
                }
            }

            if (lineStrings.Count == 0)
            {
                return [];
            }

            // Removing duplicate lines (when more than 1 match is on the same line) and grouping all matches belonging to the same line
            for (int i = 0; i < matches.Count; i++)
            {
                if (isPdfText)
                {
                    if (lineToPageMap.TryGetValue(matches[i].LineNumber, out int value))
                    {
                        pageNumber = value;
                    }
                    else
                    {
                        pageNumber = 0;
                    }
                }
                AddGrepMatch(results, matches[i], lineStrings[matches[i].LineNumber], pageNumber, false);
            }
            for (int i = 0; i < contextLines.Count; i++)
            {
                if (!results.ContainsKey(contextLines[i].LineNumber))
                    results[contextLines[i].LineNumber] = contextLines[i];
            }

            return [.. results.Values.OrderBy(l => l.LineNumber)];
        }

        private static List<GrepMatch> CloneAndSplitGroups(List<GrepMatch> bodyMatches)
        {
            string[] eolList = ["\r\n", "\n", "\r"];
            List<GrepMatch> bodyMatchesClone = new(bodyMatches);

            // split the capture groups by line to makes display formatting easier
            foreach (GrepMatch grepMatch in bodyMatchesClone)
            {
                for (int idx = 0; idx < grepMatch.Groups.Count; idx++)
                {
                    GrepCaptureGroup group = grepMatch.Groups[idx];
                    string value = group.Value;

                    foreach (string eol in eolList)
                    {
                        int pos = value.IndexOf(eol, 0, StringComparison.Ordinal);
                        if (pos > -1)
                        {
                            string name = group.Name;
                            int start = group.StartLocation;

                            string first = value[..pos];
                            string second = value[(pos + eol.Length)..];

                            grepMatch.Groups.RemoveAt(idx);
                            grepMatch.Groups.Insert(idx, new(name, start, first.Length, first, group.FullValue));
                            if (second.Length > 0 && !second.Equals(eol, StringComparison.Ordinal))
                            {
                                grepMatch.Groups.Add(new(name, start + first.Length + eol.Length, second.Length, second, group.FullValue));
                            }
                            break;
                        }
                    }
                }
            }
            return bodyMatchesClone;
        }

        public static List<GrepLine> GetLinesHexFormat(BinaryReader body, List<GrepMatch> bodyMatches, int beforeLines, int afterLines)
        {
            if (body == null || bodyMatches == null)
                return [];

            //List<GrepMatch> bodyMatchesClone = new List<GrepMatch>(bodyMatches);
            Dictionary<int, GrepLine> results = [];
            List<GrepLine> contextLines = [];
            Dictionary<int, string> lineStrings = [];
            List<int> lineNumbers = [];
            List<GrepMatch> matches = [];

            // Context line (before)
            Queue<string> beforeQueue = new();
            // Context line (after)
            int currentAfterLine = 0;
            bool startRecordingAfterLines = false;
            // Current line
            int lineNumber = 0;
            // Current index of character
            int currentIndex = 0;
            int startIndex = 0;
            int tempLinesTotalLength = 0;
            int startLine = 0;
            bool startMatched = false;
            Queue<string> lineQueue = new();

            int bufferSize = GrepSettings.Instance.Get<int>(GrepSettings.Key.HexResultByteLength);
            byte[] buffer = new byte[bufferSize];
            long length = body.BaseStream.Length;

            List<GrepMatch> bodyMatchesClone = ConvertGrepMatchesToHexLines(bodyMatches, bufferSize);

            while (body.BaseStream.Position < length && (bodyMatchesClone.Count > 0 || startRecordingAfterLines))
            {
                buffer = body.ReadBytes(bufferSize);
                string line = GetHexText(buffer);
                lineNumber++;
                bool moreMatches = true;
                // Building context queue
                if (beforeLines > 0)
                {
                    if (beforeQueue.Count >= beforeLines + 1)
                        beforeQueue.Dequeue();

                    beforeQueue.Enqueue(line.TrimEndOfLine());
                }
                if (startRecordingAfterLines && currentAfterLine < afterLines)
                {
                    currentAfterLine++;
                    contextLines.Add(new GrepLine(lineNumber, line.TrimEndOfLine(), true, null) { IsHexFile = true });
                }
                else if (currentAfterLine == afterLines)
                {
                    currentAfterLine = 0;
                    startRecordingAfterLines = false;
                }

                while (moreMatches && bodyMatchesClone.Count > 0)
                {
                    // Head of match found
                    if (bodyMatchesClone[0].StartLocation >= currentIndex && bodyMatchesClone[0].StartLocation < currentIndex + line.Length && !startMatched)
                    {
                        startMatched = true;
                        moreMatches = true;
                        lineQueue = new Queue<string>();
                        startLine = lineNumber;
                        startIndex = bodyMatchesClone[0].StartLocation - currentIndex;
                        tempLinesTotalLength = 0;

                        // Recording the before match context lines
                        while (beforeQueue.Count > 0)
                        {
                            // If only 1 line - it is the same as matched line
                            if (beforeQueue.Count == 1)
                            {
                                beforeQueue.Dequeue();
                            }
                            else
                            {
                                contextLines.Add(new GrepLine(startLine - beforeQueue.Count + 1 + (lineNumber - startLine),
                                    beforeQueue.Dequeue(), true, null)
                                {
                                    IsHexFile = true
                                });
                            }
                        }
                    }

                    // Add line to queue
                    if (startMatched)
                    {
                        lineQueue.Enqueue(line);
                        tempLinesTotalLength += line.Length;
                    }

                    // Tail of match found
                    if (bodyMatchesClone[0].StartLocation + bodyMatchesClone[0].Length <= currentIndex + line.Length && startMatched)
                    {
                        startMatched = false;
                        moreMatches = false;
                        // Start creating matches
                        for (int i = startLine; i <= lineNumber; i++)
                        {
                            lineNumbers.Add(i);
                            string tempLine = lineQueue.Dequeue();
                            lineStrings[i] = tempLine;

                            string fileMatchId = bodyMatchesClone[0].FileMatchId;
                            // First and only line
                            if (i == startLine && i == lineNumber)
                                matches.Add(new GrepMatch(fileMatchId, bodyMatchesClone[0].SearchPattern, i, startIndex, bodyMatchesClone[0].Length, bodyMatchesClone[0].Groups, bodyMatchesClone[0].RegexMatchValue));
                            // First but not last line
                            else if (i == startLine)
                                matches.Add(new GrepMatch(fileMatchId, bodyMatchesClone[0].SearchPattern, i, startIndex, tempLine.TrimEndOfLine().Length - startIndex, bodyMatchesClone[0].Groups, bodyMatchesClone[0].RegexMatchValue));
                            // Middle line
                            else if (i > startLine && i < lineNumber)
                                matches.Add(new GrepMatch(fileMatchId, bodyMatchesClone[0].SearchPattern, i, 0, tempLine.TrimEndOfLine().Length, bodyMatchesClone[0].Groups, bodyMatchesClone[0].RegexMatchValue));
                            // Last line
                            else
                                matches.Add(new GrepMatch(fileMatchId, bodyMatchesClone[0].SearchPattern, i, 0, bodyMatchesClone[0].Length - tempLinesTotalLength + line.Length + startIndex, bodyMatchesClone[0].Groups, bodyMatchesClone[0].RegexMatchValue));

                            startRecordingAfterLines = true;
                        }
                        bodyMatchesClone.RemoveAt(0);
                    }

                    // Another match on this line
                    if (bodyMatchesClone.Count > 0 && bodyMatchesClone[0].StartLocation >= currentIndex && bodyMatchesClone[0].StartLocation < currentIndex + line.Length && !startMatched)
                        moreMatches = true;
                    else
                        moreMatches = false;
                }

                currentIndex += line.Length;
            }

            if (lineStrings.Count == 0)
            {
                return [];
            }

            // Removing duplicate lines (when more than 1 match is on the same line) and grouping all matches belonging to the same line
            for (int i = 0; i < matches.Count; i++)
            {
                AddGrepMatch(results, matches[i], lineStrings[matches[i].LineNumber], -1, true);
            }
            for (int i = 0; i < contextLines.Count; i++)
            {
                if (!results.ContainsKey(contextLines[i].LineNumber))
                    results[contextLines[i].LineNumber] = contextLines[i];
            }

            return [.. results.Values.OrderBy(l => l.LineNumber)];
        }

        private static List<GrepMatch> ConvertGrepMatchesToHexLines(List<GrepMatch> bodyMatches, int bufferSize)
        {
            // 2 digit hex number plus space for each byte
            // and trailing space is removed
            int lineLength = bufferSize * 3 - 1;

            List<GrepMatch> list = [];
            foreach (GrepMatch match in bodyMatches)
            {
                int lineNum = match.StartLocation / bufferSize;
                int lineStart = match.StartLocation % bufferSize;
                int startLocation = lineNum * lineLength + lineStart * 3;
                int matchLength = match.Length * 3 - 1;

                int newLines = (match.StartLocation % bufferSize + match.Length - 1) / bufferSize;
                matchLength -= newLines;

                list.Add(new GrepMatch(match.SearchPattern, lineNum, startLocation, matchLength, match.RegexMatchValue));
            }
            return list;
        }

        private static string GetHexText(byte[] buffer)
        {
            StringBuilder sb = new();

            for (int idx = 0; idx < buffer.Length; idx++)
            {
                sb.AppendFormat("{0:x2}", buffer[idx]).Append(' ');
            }

            return sb.ToString().TrimEnd();
        }

        private static void AddGrepMatch(Dictionary<int, GrepLine> lines, GrepMatch match, string lineText, int pageNumber, bool isHexFile)
        {
            if (!lines.ContainsKey(match.LineNumber))
                lines[match.LineNumber] = new GrepLine(match.LineNumber, lineText.TrimEndOfLine(), false, null) { PageNumber = pageNumber, IsHexFile = isHexFile };
            lines[match.LineNumber].Matches.Add(match);
        }

        /// <summary>
        /// Replaces unix-style linebreaks with \r\n
        /// </summary>
        /// <param name="text"></param>
        /// <returns></returns>
        public static string CleanLineBreaks(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;
            string textTemp = UnixEolRegex1().Replace(text, "\r\n$2");
            textTemp = UnixEolRegex2().Replace(textTemp, "$1\r\n");
            textTemp = UnixEolRegex3().Replace(textTemp, "\r\n");
            return textTemp;
        }

        /// <summary>
        /// Returns MD5 hash for string
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        public static string GetHash(string input)
        {
            byte[] inputBytes = Encoding.ASCII.GetBytes(input);
            byte[] hash = MD5.HashData(inputBytes);
            return Convert.ToHexString(hash);
        }

        /// <summary>
        /// Returns true if beginText end with a non-alphanumeric character. Copied from AstroGrep.
        /// </summary>
        /// <param name="beginText">Text to test</param>
        /// <returns></returns>
        public static bool IsValidBeginText(string beginText)
        {
            if (beginText == null)
                return false;

            if (beginText.Equals(string.Empty, StringComparison.Ordinal) ||
               beginText.EndsWith(' ') ||
               beginText.EndsWith('<') ||
               beginText.EndsWith('>') ||
               beginText.EndsWith('$') ||
               beginText.EndsWith('+') ||
               beginText.EndsWith('*') ||
               beginText.EndsWith('[') ||
               beginText.EndsWith('{') ||
               beginText.EndsWith('(') ||
               beginText.EndsWith('.') ||
               beginText.EndsWith('?') ||
               beginText.EndsWith('!') ||
               beginText.EndsWith(',') ||
               beginText.EndsWith(':') ||
               beginText.EndsWith(';') ||
               beginText.EndsWith('-') ||
               beginText.EndsWith('=') ||
               beginText.EndsWith('\\') ||
               beginText.EndsWith('/') ||
               beginText.EndsWith('\'') ||
               beginText.EndsWith('"') ||
               beginText.EndsWith(Environment.NewLine, StringComparison.Ordinal) ||
               beginText.EndsWith("\r\n", StringComparison.Ordinal) ||
               beginText.EndsWith('\r') ||
               beginText.EndsWith('\n') ||
               beginText.EndsWith('\t'))
            {
                return true;
            }

            return false;
        }

        public static string ReplaceSpecialCharacters(string input)
        {
            if (string.IsNullOrEmpty(input))
                return string.Empty;

            string result = input.Replace(@"\\a", "\a", StringComparison.Ordinal)
                                 .Replace(@"\\b", "\b", StringComparison.Ordinal)
                                 .Replace(@"\\f", "\f", StringComparison.Ordinal)
                                 .Replace(@"\\n", "\n", StringComparison.Ordinal)
                                 .Replace(@"\\r", "\r", StringComparison.Ordinal)
                                 .Replace(@"\\t", "\t", StringComparison.Ordinal)
                                 .Replace(@"\\v", "\v", StringComparison.Ordinal)
                                 .Replace(@"\\0", "\0", StringComparison.Ordinal);
            return result;
        }

        /// <summary>
        /// Returns true if endText starts with a non-alphanumeric character. Copied from AtroGrep.
        /// </summary>
        /// <param name="endText"></param>
        /// <returns></returns>
        public static bool IsValidEndText(string endText)
        {
            if (endText == null)
                return false;

            if (endText.Equals(string.Empty, StringComparison.Ordinal) ||
               endText.StartsWith(' ') ||
               endText.StartsWith('<') ||
               endText.StartsWith('$') ||
               endText.StartsWith('+') ||
               endText.StartsWith('*') ||
               endText.StartsWith('[') ||
               endText.StartsWith('{') ||
               endText.StartsWith('(') ||
               endText.StartsWith('.') ||
               endText.StartsWith('?') ||
               endText.StartsWith('!') ||
               endText.StartsWith(',') ||
               endText.StartsWith(':') ||
               endText.StartsWith(';') ||
               endText.StartsWith('-') ||
               endText.StartsWith('=') ||
               endText.StartsWith('>') ||
               endText.StartsWith(']') ||
               endText.StartsWith('}') ||
               endText.StartsWith(')') ||
               endText.StartsWith('\\') ||
               endText.StartsWith('/') ||
               endText.StartsWith('\'') ||
               endText.StartsWith('"') ||
               endText.StartsWith(Environment.NewLine, StringComparison.Ordinal) ||
               endText.StartsWith("\r\n", StringComparison.Ordinal) ||
               endText.StartsWith('\r') ||
               endText.StartsWith('\n') ||
               endText.StartsWith('\t'))
            {
                return true;
            }

            return false;
        }


        /// <summary>
        /// Extension method on TimeSpan that gets a "pretty", human readable string of a TimeSpan, e.g. "1h 23m 45.678s".
        /// Hours and minutes are left off as not needed. Hours are the largest unit of time shown (e.g. not days, weeks).
        /// </summary>
        /// <param name="duration">The time span in question.</param>
        /// <returns>"Pretty", human readable string of the time span.</returns>
        public static string GetPrettyString(this TimeSpan duration)
        {
            var durationStringBuilder = new StringBuilder();
            var totalHoursTruncated = (int)duration.TotalHours;

            if (totalHoursTruncated > 0)
                durationStringBuilder.Append(totalHoursTruncated + "h ");

            if (duration.Minutes > 0 || totalHoursTruncated > 0)
                durationStringBuilder.Append(duration.Minutes + "m ");

            durationStringBuilder.Append(duration.Seconds + "." + duration.Milliseconds + "s");

            return durationStringBuilder.ToString();
        }

        public static bool HasUtf8ByteOrderMark(string srcFile)
        {
            using FileStream readStream = File.Open(srcFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return HasUtf8ByteOrderMark(readStream);
        }

        public static bool HasUtf8ByteOrderMark(Stream inputStream)
        {
            int b1 = inputStream.ReadByte();
            int b2 = inputStream.ReadByte();
            int b3 = inputStream.ReadByte();
            inputStream.Seek(0, SeekOrigin.Begin);

            return 0xEF == b1 && 0xBB == b2 && 0xBF == b3;
        }

        public static string GetTempTextFileName(string filePath)
        {
            if (!string.IsNullOrEmpty(filePath))
            {
                return string.Concat(Path.GetFileName(filePath), "_", HashSHA256(filePath), ".txt");
            }
            return string.Empty;
        }

        public static string GetTempTextFileName(Stream stream, string fileName)
        {
            if (stream != null && !string.IsNullOrEmpty(fileName))
            {
                return string.Concat(Path.GetFileName(fileName), "_", HashSHA256(stream), ".txt");
            }
            return string.Empty;
        }

        public static string HashSHA256(string file)
        {
            try
            {
                using var stream = File.OpenRead(file);
                return Convert.ToHexString(SHA256.HashData(stream));
            }
            catch (Exception) // cannot open file for reading
            {
                return string.Empty;
            }
        }

        public static string HashSHA256(Stream stream)
        {
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            stream.Seek(0, SeekOrigin.Begin);
            return hash;
        }

        public static string GetTempTextFileName(FileData fileData)
        {
            if (fileData != null)
            {
                return string.Concat(Path.GetFileName(fileData.Name), "_", HashFileSizeModifiedTime(fileData), ".txt");
            }
            return string.Empty;
        }

        public static string HashFileSizeModifiedTime(FileData fileData)
        {
            long size = fileData.Length;
            long ticks = fileData.LastWriteTimeUtc.Ticks;

            byte[] input = [.. BitConverter.GetBytes(size), .. BitConverter.GetBytes(ticks)];
            return Convert.ToHexString(SHA256.HashData(input));
        }

        /// <summary>
        /// Gets a value that indicates whether <paramref name="path"/>
        /// is a valid path.
        /// </summary>
        /// <returns>Returns <c>true</c> if <paramref name="path"/> is a
        /// valid path; <c>false</c> otherwise. Also returns <c>false</c> if
        /// the caller does not have the required permissions to access
        /// <paramref name="path"/>.
        /// </returns>
        /// <seealso cref="Path.GetFullPath"/>
        /// <seealso cref="TryGetFullPath"/>
        public static bool IsValidPath(string path)
        {
            return TryGetFullPath(path, out _);
        }

        /// <summary>
        /// Returns the absolute path for the specified path string. A return
        /// value indicates whether the conversion succeeded.
        /// </summary>
        /// <param name="path">The file or directory for which to obtain absolute
        /// path information.
        /// </param>
        /// <param name="result">When this method returns, contains the absolute
        /// path representation of <paramref name="path"/>, if the conversion
        /// succeeded, or <see cref="string.Empty"/> if the conversion failed.
        /// The conversion fails if <paramref name="path"/> is null or
        /// <see cref="string.Empty"/>, or is not of the correct format. This
        /// parameter is passed uninitialized; any value originally supplied
        /// in <paramref name="result"/> will be overwritten.
        /// </param>
        /// <returns><c>true</c> if <paramref name="path"/> was converted
        /// to an absolute path successfully; otherwise, false.
        /// </returns>
        /// <seealso cref="Path.GetFullPath"/>
        /// <seealso cref="IsValidPath"/>
        public static bool TryGetFullPath(string path, out string result)
        {
            result = string.Empty;
            if (string.IsNullOrWhiteSpace(path)) { return false; }
            bool status = false;

            try
            {
                result = Path.GetFullPath(path);
                status = true;
            }
            catch (ArgumentException) { }
            catch (SecurityException) { }
            catch (NotSupportedException) { }
            catch (PathTooLongException) { }

            return status;
        }
        public static bool IsGitInstalled => GitUtil.IsGitInstalled;

        public static bool ValidateRegex(string pattern)
        {
            try
            {
                Regex regex = new(pattern);
                return true;
            }
            catch
            {
                return false;
            }
        }

        [GeneratedRegex("\\p{IsArabic}|\\p{IsHebrew}")]
        private static partial Regex RtlRegex();

        [GeneratedRegex("(\r)([^\n])")]
        private static partial Regex UnixEolRegex1();

        [GeneratedRegex("([^\r])(\n)")]
        private static partial Regex UnixEolRegex2();

        [GeneratedRegex("(\v)")]
        private static partial Regex UnixEolRegex3();

        [GeneratedRegex(@"\[(\w+)\]")]
        private static partial Regex PatternTypeRegex();

        internal const string NoExtensionPattern = @"(?<!\.\w+)$";
        [GeneratedRegex(NoExtensionPattern)]
        private static partial Regex NoExtensionRegex();

        internal const string DotFilesPattern = @"\\\.[^\\]+$";
        [GeneratedRegex(DotFilesPattern)]
        private static partial Regex DotFilesRegex();
    }

    public class KeyValueComparer : IComparer<KeyValuePair<string, int>>
    {
        public int Compare(KeyValuePair<string, int> x, KeyValuePair<string, int> y)
        {
            return string.Compare(x.Key, y.Key, StringComparison.Ordinal);
        }
    }

    public static class StringExtensions
    {
        public static string TrimEndOfLine(this string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;
            if (text.EndsWith("\r\n", StringComparison.Ordinal))
                return text[..^2];
            else if (text.EndsWith('\r'))
                return text[..^1];
            else if (text.EndsWith('\n'))
                return text[..^1];
            else
                return text;
        }
    }
}
