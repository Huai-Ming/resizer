/**
 * Written by Nathanael Jones 
 * http://nathanaeljones.com
 * nathanael.jones@gmail.com
 * 
 * Although I typically release my components for free, I decided to charge a 
 * 'download fee' for this one to help support my other open-source projects. 
 * Don't worry, this component is still open-source, and the license permits 
 * source redistribution as part of a larger system. However, I'm asking that 
 * people who want to integrate this component purchase the download instead 
 * of ripping it out of another open-source project. My free to non-free LOC 
 * (lines of code) ratio is still over 40 to 1, and I plan on keeping it that 
 * way. I trust this will keep everybody happy.
 * 
 * By purchasing the download, you are permitted to 
 * 
 * 1) Modify and use the component in all of your projects. 
 * 
 * 2) Redistribute the source code as part of another project, provided 
 * the component is less than 5% of the project (in lines of code), 
 * and you keep this information attached.
 * 
 * 3) If you received the source code as part of another open source project, 
 * you cannot extract it (by itself) for use in another project without purchasing a download 
 * from http://nathanaeljones.com/. If nathanaeljones.com is no longer running, and a download
 * cannot be purchased, then you may extract the code.
 * 
 **/


using System;
using System.Collections.Generic;
using System.Text;
using System.Web;
using System.Configuration;
using fbs;
using System.IO;

namespace fbs.ImageResizer
{
    /// <summary>
    /// Indicates a problem with disk caching. Causes include a missing (or too small) ImageDiskCacheDir setting, and severe I/O locking preventing 
    /// the cache dir from being cleaned at all.
    /// </summary>
    public class DiskCacheException : Exception
    {
        public DiskCacheException(string message):base(message){}
        public DiskCacheException(string message, Exception innerException) : base(message, innerException) { }
    }
    /// <summary>
    /// Provides methods for creating, maintaining, and securing the disk cache. 
    /// </summary>
    public class DiskCache
    {
        /// <summary>
        /// A callback method that will perform the resize and update the file. This doesn't need paramaters since an anonymous function can be used.
        /// </summary>
        public delegate void CacheUpdateCallback();
        /// <summary>
        /// Checks if an update is needed on the specified file... calls the method if needed.
        /// TODO: Implement locking to prevent I/O conflicts on concurrent inital request
        /// </summary>
        /// <param name="sourceFilename"></param>
        /// <param name="cachedFilename"></param>
        /// <param name="updateCallback"></param>
        public static void UpdateCachedVersionIfNeeded(string sourceFilename, string cachedFilename, CacheUpdateCallback updateCallback)
        {
            PrepareCacheDir();
            //TODO - implement locking so concurrent requests for the same file don't cause an I/O issue.
            if (!IsCachedVersionValid(sourceFilename, cachedFilename))
            {
                updateCallback();
                //Update the write time to match - this is how we know whether they are in sync.
                System.IO.File.SetLastWriteTimeUtc(cachedFilename, System.IO.File.GetLastWriteTimeUtc(sourceFilename));
            }
        }

        private static void LogWarning(String message)
        {
            HttpContext.Current.Trace.Warn("ImageResizer", message);
        }
        private static void LogException(Exception e)
        {
            HttpContext.Current.Trace.Warn("ImageResizer", e.Message, e);// Event.CreateExceptionEvent(e).SaveAsync();
        }


        /// <summary>
        /// Assumes localSourceFile exists. Returns true if localCachedFile exists and matches the last write time of localSourceFile.
        /// </summary>
        /// <param name="localSourceFile">full physical path of original file</param>
        /// <param name="cachedFilename">full physical path of cached file.</param>
        /// <returns></returns>
        public static bool IsCachedVersionValid(string localSourceFile, string localCachedFile){
            if (localCachedFile == null) return false;
            if (!System.IO.File.Exists(localCachedFile)) return false;


            //When we save thumbnail files to disk, we set the write time to that of the source file.
            //This allows us to track if the source file has changed.

            DateTime cached = System.IO.File.GetLastWriteTimeUtc(localCachedFile);
            DateTime source = System.IO.File.GetLastWriteTimeUtc(localSourceFile);
            return RoughCompare(cached, source);
        }

        /// <summary>
        /// This string contains the contents of a web.conig file that sets URL authorization to "deny all" inside the current directory.
        /// </summary>
        private const string webConfigFile =
            "<?xml version=\"1.0\"?>" +
            "<configuration xmlns=\"http://schemas.microsoft.com/.NetConfiguration/v2.0\">" +
            "<system.web><authorization>" +
            "<deny users=\"*\" />" +
            "</authorization></system.web></configuration>";


        /// <summary>
        /// Creates the directory for caching if needed, and performs 'garbage collection'
        /// Throws a DiskCacheException if the cache direcotry isn't specified in web.config
        /// Creates a web.config file in the caching directory to prevent direct access.
        /// </summary>
        /// <returns></returns>
        public static void PrepareCacheDir()
        {
            string dir = GetCacheDir();

            //Create the cache directory if it doesn't exist.
            if (!System.IO.Directory.Exists(dir))
                System.IO.Directory.CreateDirectory(dir);

            //Add URL authorization protection using a web.config file.
            yrl wc = yrl.Combine(new yrl(dir), new yrl("Web.config"));
            if (!wc.FileExists)
            {
                System.IO.File.WriteAllText(wc.Local, webConfigFile);
            }

            //Perform cleanup if needed. Clear 1/10 of the files if we are running low.
            int maxCount = GetMaxCachedFiles();
            TrimDirectoryFiles(dir, maxCount - 1, (maxCount / 10));

        }

        /// <summary>
        /// Returns the physical path of the image cache dir. Calcualted from AppSettings["ImageDiskCacheDir"] (yrl form). throws an exception if missing
        /// </summary>
        /// <returns></returns>
        public static string GetCacheDir()
        {
            string dir = ConfigurationManager.AppSettings["ImageDiskCacheDir"];
            yrl conv = null;
            if (!string.IsNullOrEmpty(dir)) conv = yrl.FromString(dir);

            if (string.IsNullOrEmpty(dir) || yrl.IsNullOrEmpty(conv))
            {
                throw new DiskCacheException("The 'ImageDiskCacheDir' setting is missing from web.config. A directory name is required for image caching to work.");
            }
            return conv.Local;
        }

        /// <summary>
        /// Deletes least-used files from the directory (if needed)
        /// Throws an exception if cleanup fails.
        /// Returns true if any files were deleted.
        /// </summary>
        /// <param name="dir">The directory to clean up</param>
        /// <param name="maxCount">The maximum number of files to leave in the directory. Does nothing if this is less than 0</param>
        /// <param name="deleteExtra">How many extra files to delete if deletions are required</param>
        /// <returns></returns>
        public static bool TrimDirectoryFiles(string dir, int maxCount, int deleteExtra)
        {
            if (maxCount < 0) return false;

            // if (deleteExtra > maxCount) throw warning

            string[] files = System.IO.Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
            if (files.Length <= maxCount) return false;

            //Oops, look like we have to clean up a little.

            List<KeyValuePair<string, DateTime>> fileinfo = new List<KeyValuePair<string, DateTime>>(files.Length);

            //Sory by last access time
            foreach (string s in files)
            {
                fileinfo.Add(new KeyValuePair<string, DateTime>(s, System.IO.File.GetLastAccessTimeUtc(s)));
            }
            fileinfo.Sort(CompareFiles);


            int deleteCount = files.Length - maxCount + deleteExtra;

            bool deletedSome = false;
            //Delete files, Least recently used order
            for (int i = 0; i < deleteCount && i < fileinfo.Count; i++)
            {
                //Never delete .config files
                if (System.IO.Path.GetExtension(fileinfo[i].Key).Equals("config", StringComparison.OrdinalIgnoreCase)){
                    deleteCount++; //Just delete an extra file instead...
                    continue;
                }
                //All other files can be deleted.
                try
                {
                    System.IO.File.Delete(fileinfo[i].Key);
                    deletedSome = true;
                }
                catch (IOException ioe)
                {
                    if (i >= fileinfo.Count - 1)
                    {
                        //Looks like we're at the end.
                        throw new DiskCacheException("Couldn't delete enough files to make room for new images. Please increase MaxCachedFiles or solve I/O isses/", ioe);
                       
                    }

                    //Try an extra candidate to make up for this missing one
                    deleteCount++;
                    LogException(ioe);

                }
            }
            return deletedSome;
        }
        /// <summary>
        /// Compares the file dates on the arguments
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <returns></returns>
        private static int CompareFiles(KeyValuePair<string, DateTime> x, KeyValuePair<string, DateTime> y)
        {
            return x.Value.CompareTo(y.Value);
        }


        /// <summary>
        /// Returns true if both dates are equal (to the nearest 200th of a second)
        /// </summary>
        /// <param name="modifiedOn"></param>
        /// <param name="dateTime"></param>
        /// <returns></returns>
        private static bool RoughCompare(DateTime d1, DateTime d2)
        {
            return (new TimeSpan((long)Math.Abs(d1.Ticks - d2.Ticks)).Milliseconds <= 5);
        }



        /// <summary>
        /// Returns true if successful, false if not.
        /// </summary>
        /// <param name="context"></param>
        /// <param name="identifier"></param>
        /// <returns></returns>
       /* public static bool SendDiskCache(HttpContext context, string identifier)
        {
            string filename = GetFilenameFromId(identifier);
            if (filename == null) return false;
            if (!System.IO.File.Exists(filename)) return false;

            context.Response.TransmitFile(filename);
            return true;
        }*/

        /// <summary>
        /// Clears the cache directory. Returns true if successful. (In-use files make this rare).
        /// </summary>
        /// <returns></returns>
        public static bool ClearCacheDir()
        {
            return TrimDirectoryFiles(GetCacheDir(), 0, 0);
        }
        /// <summary>
        /// Returns the value of AppSettings["ImageResizerMaxWidth"], or 640 if the setting is missing
        /// </summary>
        /// <returns></returns>
        public static int GetMaxWidth()
        {
            int maxwidth = 0;
            if (!string.IsNullOrEmpty(ConfigurationManager.AppSettings["ImageResizerMaxWidth"]))
            {
                if (int.TryParse(ConfigurationManager.AppSettings["ImageResizerMaxWidth"], out maxwidth))
                {
                    return maxwidth;
                }
            }
            return 1680;
        }
        /// <summary>
        /// Returns the value of AppSettings["ImageResizerMaxHeight"], or 480 if the setting is missing
        /// </summary>
        /// <returns></returns>
        public static int GetMaxHeight()
        {
            int maxheight = 0;
            if (!string.IsNullOrEmpty(ConfigurationManager.AppSettings["ImageResizerMaxHeight"]))
            {
                if (int.TryParse(ConfigurationManager.AppSettings["ImageResizerMaxHeight"], out maxheight))
                {
                    return maxheight;
                }
            }
            return 1680;
        }
        /// <summary>
        /// Returns the value of required setting AppSettings["MaxCachedImages"], or 400 if it is missing. An event will be logged if it is missing.
        /// </summary>
        /// <returns></returns>
        public static int GetMaxCachedFiles()
        {
            string limit = ConfigurationManager.AppSettings["MaxCachedImages"];
            int maxCount = 400;
            if (!int.TryParse(limit, out maxCount))
            {
                maxCount = 400;
                LogWarning("No value specified for application setting MaxCachedImages. Defaulting to " + maxCount.ToString() + ". A maximum of " + maxCount.ToString() + " images will be allowed in the cache (cycling will occurr).");
            }
            return maxCount;
        }

        /// <summary>
        /// Returns true if the image caching directory (GetCacheDir()) exists.
        /// </summary>
        /// <returns></returns>
        public static bool CacheDirExists()
        {
            string dir = GetCacheDir();
            if (!string.IsNullOrEmpty(dir))
            {
                if (!System.IO.Directory.Exists(dir))
                {
                    return false;
                }

                return true;
            }
            return false;
        }
        /// <summary>
        /// Returns the number of files inside the image cache directory (recursive traversal)
        /// </summary>
        /// <returns></returns>
        public static int GetCacheDirFilesCount()
        {
            if (CacheDirExists())
            {
                string dir = GetCacheDir();
                string[] files = System.IO.Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
                return files.Length;
            }
            return 0;
        }
        /// <summary>
        /// Returns the summation of the size of the indiviual files in the image cache directory (recursive traversal)
        /// </summary>
        /// <returns></returns>
        public static long GetCacheDirTotalSize()
        {
            if (CacheDirExists())
            {
                string dir = GetCacheDir();
                long totalSize = 0;
                string[] files = System.IO.Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
                foreach (string s in files)
                {

                    FileInfo fi = new FileInfo(s);
                    totalSize += fi.Length;
                }
                return totalSize;
            }
            return 0;

        }
        /// <summary>
        /// Returns the average size of a file in the image cache directory. Expensive, calls GetCacheDirFilesCount() and GetCacheDirTotalSize()
        /// </summary>
        /// <returns></returns>
        public static int GetAverageCachedFileSize()
        {
            double files = GetCacheDirFilesCount();
            if (files < 1) return 0;

            return (int)Math.Round((double)GetCacheDirTotalSize() / files);
        }


    }
}
