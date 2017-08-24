﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using TumblThree.Applications.DataModels;
using TumblThree.Applications.Properties;
using TumblThree.Applications.Services;
using TumblThree.Domain;
using TumblThree.Domain.Models;

namespace TumblThree.Applications.Downloader
{
    public abstract class AbstractDownloader : IDownloader
    {
        protected readonly IBlog blog;
        protected readonly ICrawlerService crawlerService;
        protected readonly PostCounter counter;
        protected readonly IFiles files;
        protected readonly IProgress<DownloadProgress> progress;
        protected readonly object lockObjectDb = new object();
        protected readonly object lockObjectDirectory = new object();
        protected readonly object lockObjectDownload = new object();
        protected readonly object lockObjectProgress = new object();
        protected readonly BlockingCollection<TumblrPost> producerConsumerCollection;
        protected readonly IShellService shellService;
        protected readonly CancellationToken ct;
        protected readonly PauseToken pt;
        protected readonly FileDownloader fileDownloader;

        protected AbstractDownloader(IShellService shellService, CancellationToken ct, PauseToken pt, IProgress<DownloadProgress> progress, PostCounter counter, BlockingCollection<TumblrPost> producerConsumerCollection, FileDownloader fileDownloader, ICrawlerService crawlerService = null, IBlog blog = null, IFiles files = null)
        {
            this.shellService = shellService;
            this.crawlerService = crawlerService;
            this.blog = blog;
            this.ct = ct;
            this.pt = pt;
            this.progress = progress;
            this.files = files;
            this.counter = counter;
            this.producerConsumerCollection = producerConsumerCollection;
            this.fileDownloader = fileDownloader;
        }

        private bool CreateDataFolder()
        {
            if (string.IsNullOrEmpty(blog.Name))
            {
                return false;
            }

            string blogPath = blog.DownloadLocation();

            if (!Directory.Exists(blogPath))
            {
                Directory.CreateDirectory(blogPath);
                return true;
            }
            return true;
        }

        protected virtual bool CheckIfFileExistsInDB(string url)
        {
            string fileName = url.Split('/').Last();
            Monitor.Enter(lockObjectDb);
            if (files.Links.Contains(fileName))
            {
                Monitor.Exit(lockObjectDb);
                return true;
            }
            Monitor.Exit(lockObjectDb);
            return false;
        }

        protected virtual bool CheckIfBlogShouldCheckDirectory(string url)
        {
            if (blog.CheckDirectoryForFiles)
            {
                return CheckIfFileExistsInDirectory(url);
            }
            return false;
        }

        protected virtual bool CheckIfFileExistsInDirectory(string url)
        {
            string fileName = url.Split('/').Last();
            Monitor.Enter(lockObjectDirectory);
            string blogPath = blog.DownloadLocation();
            if (File.Exists(Path.Combine(blogPath, fileName)))
            {
                Monitor.Exit(lockObjectDirectory);
                return true;
            }
            Monitor.Exit(lockObjectDirectory);
            return false;
        }

        public void UpdateProgressQueueInformation(string format, params object[] args)
        {
            var newProgress = new DataModels.DownloadProgress
            {
                Progress = string.Format(CultureInfo.CurrentCulture, format, args)
            };
            progress.Report(newProgress);
        }

        protected virtual string GetCoreImageUrl(string url)
        {
            return url;
        }

        protected virtual async Task<bool> DownloadBinaryFile(string fileLocation, string url)
        {
            try
            {
                return await fileDownloader.DownloadFileWithResumeAsync(url, fileLocation);
            }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) == 0x27 || (ex.HResult & 0xFFFF) == 0x70)
            {
                // Disk Full, HRESULT: ‭-2147024784‬ == 0xFFFFFFFF80070070
                Logger.Error("Downloader:DownloadBinaryFile: {0}", ex);
                shellService.ShowError(ex, Resources.DiskFull);
                crawlerService.StopCommand.Execute(null);
                return false;
            }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) == 0x20)
            {
                // The process cannot access the file because it is being used by another process.", HRESULT: -2147024864 == 0xFFFFFFFF80070020
                return true;
            }
            catch (WebException webException) when ((webException.Response != null))
            {
                var webRespStatusCode = (int)((HttpWebResponse)webException?.Response).StatusCode;
                if (webRespStatusCode >= 400 && webRespStatusCode < 600) // removes inaccessible files: http status codes 400 to 599
                {
                    try { File.Delete(fileLocation); } // could be open again in a different thread
                    catch { }
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        protected virtual async Task<bool> DownloadBinaryFile(string fileLocation, string fileLocationUrlList, string url)
        {
            if (!blog.DownloadUrlList)
            {
                return await DownloadBinaryFile(fileLocation, url);
            }
            else
            {
                return AppendToTextFile(fileLocationUrlList, url);
            }
        }

        protected virtual bool AppendToTextFile(string fileLocation, string text)
        {
            try
            {
                // better not await in the lock?
                lock (lockObjectDownload)
                {
                    using (var sw = new StreamWriter(fileLocation, true))
                    {
                        sw.WriteLine(text);
                    }
                }
                return true;
            }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) == 0x27 || (ex.HResult & 0xFFFF) == 0x70)
            {
                Logger.Error("Downloader:AppendToTextFile: {0}", ex);
                shellService.ShowError(ex, Resources.DiskFull);
                crawlerService.StopCommand.Execute(null);
                return false;
            }
            catch
            {
                return false;
            }
        }

        protected virtual void UpdateBlogProgress(ref int totalCounter)
        {
            blog.DownloadedImages = Interlocked.Increment(ref totalCounter);
            blog.Progress = (int)((double)totalCounter / (double)blog.TotalCount * 100);
        }

        protected virtual void UpdateBlogPostCount(ref int postCounter, Action<int> blogCounter)
        {
            blogCounter(Interlocked.Increment(ref postCounter));
        }

        protected virtual void UpdateBlogDB(string fileName)
        {
            lock (lockObjectProgress)
            {
                files.Links.Add(fileName);
            }
        }

        public virtual async Task<bool> DownloadBlogAsync()
        {
            var semaphoreSlim = new SemaphoreSlim(shellService.Settings.ParallelImages / crawlerService.ActiveItems.Count);
            var trackedTasks = new List<Task>();
            var completeDownload = true;

            CreateDataFolder();

            foreach (TumblrPost downloadItem in producerConsumerCollection.GetConsumingEnumerable())
            {
                await semaphoreSlim.WaitAsync();

                if (ct.IsCancellationRequested)
                {
                    break;
                }
                if (pt.IsPaused)
                {
                    pt.WaitWhilePausedWithResponseAsyc().Wait();
                }

                trackedTasks.Add(new Func<Task>(async () =>
                {
                    try { await DownloadPostAsync(downloadItem); }
                    catch {}
                    finally { semaphoreSlim.Release(); }
                })());
            }
            try { await Task.WhenAll(trackedTasks); }
            catch { completeDownload = false; }

            blog.LastDownloadedPhoto = null;
            blog.LastDownloadedVideo = null;

            files.Save();

            return completeDownload;
        }

        private async Task DownloadPostAsync(TumblrPost downloadItem)
        {
            switch (downloadItem.PostType)
            {
                case PostTypes.Photo:
                    await DownloadPhotoAsync(downloadItem);
                    break;
                case PostTypes.Video:
                    await DownloadVideoAsync(downloadItem);
                    break;
                case PostTypes.Audio:
                    await DownloadAudioAsync(downloadItem);
                    break;
                case PostTypes.Text:
                    DownloadText(downloadItem);
                    break;
                case PostTypes.Quote:
                    DownloadQuote(downloadItem);
                    break;
                case PostTypes.Link:
                    DownloadLink(downloadItem);
                    break;
                case PostTypes.Conversation:
                    DownloadConversation(downloadItem);
                    break;
                case PostTypes.Answer:
                    DownloadAnswer(downloadItem);
                    break;
                case PostTypes.PhotoMeta:
                    DownloadPhotoMeta(downloadItem);
                    break;
                case PostTypes.VideoMeta:
                    DownloadVideoMeta(downloadItem);
                    break;
                case PostTypes.AudioMeta:
                    DownloadAudioMeta(downloadItem);
                    break;
                default:
                    break;
            }
        }

        protected virtual async Task DownloadPhotoAsync(TumblrPost downloadItem)
        {
            string blogDownloadLocation = blog.DownloadLocation();
            string url = Url(downloadItem);
            string fileName = url.Split('/').Last();
            string fileLocation = FileLocation(blogDownloadLocation, fileName);
            string fileLocationUrlList = FileLocationLocalized(blogDownloadLocation, Resources.FileNamePhotos);
            DateTime postDate = PostDate(downloadItem);

            if (!(CheckIfFileExistsInDB(url) || CheckIfBlogShouldCheckDirectory(GetCoreImageUrl(url))))
            {
                UpdateProgressQueueInformation(Resources.ProgressDownloadImage, fileName);
                if (await DownloadBinaryFile(fileLocation, fileLocationUrlList, url))
                {
                    SetFileDate(fileLocation, postDate);
                    UpdateBlogPostCount(ref counter.Photos, value => blog.DownloadedPhotos = value);
                    UpdateBlogProgress(ref counter.TotalDownloads);
                    UpdateBlogDB(fileName);
                    if (shellService.Settings.EnablePreview)
                    {
                        if (!fileName.EndsWith(".gif"))
                        {
                            blog.LastDownloadedPhoto = Path.GetFullPath(fileLocation);
                        }
                        else
                        {
                            blog.LastDownloadedVideo = Path.GetFullPath(fileLocation);
                        }
                    }
                }
            }
        }

        private async Task DownloadVideoAsync(TumblrPost downloadItem)
        {
            string blogDownloadLocation = blog.DownloadLocation();
            string fileName = FileName(downloadItem);
            string url = Url(downloadItem);
            string fileLocation = FileLocation(blogDownloadLocation, fileName);
            string fileLocationUrlList = FileLocationLocalized(blogDownloadLocation, Resources.FileNameVideos);
            DateTime postDate = PostDate(downloadItem);

            if (!(CheckIfFileExistsInDB(url) || CheckIfBlogShouldCheckDirectory(url)))
            {
                UpdateProgressQueueInformation(Resources.ProgressDownloadImage, fileName);
                if (await DownloadBinaryFile(fileLocation, fileLocationUrlList, url))
                {
                    SetFileDate(fileLocation, postDate);
                    UpdateBlogPostCount(ref counter.Videos, value => blog.DownloadedVideos = value);
                    UpdateBlogProgress(ref counter.TotalDownloads);
                    UpdateBlogDB(fileName);
                    if (shellService.Settings.EnablePreview)
                    {
                        blog.LastDownloadedVideo = Path.GetFullPath(fileLocation);
                    }
                }
            }
        }

        private async Task DownloadAudioAsync(TumblrPost downloadItem)
        {
            string blogDownloadLocation = blog.DownloadLocation();
            string url = Url(downloadItem);
            string fileName = FileName(downloadItem);
            string fileLocation = FileLocation(blogDownloadLocation, fileName);
            string fileLocationUrlList = FileLocationLocalized(blogDownloadLocation, Resources.FileNameAudios);
            DateTime postDate = PostDate(downloadItem);

            if (!(CheckIfFileExistsInDB(url) || CheckIfBlogShouldCheckDirectory(url)))
            {
                UpdateProgressQueueInformation(Resources.ProgressDownloadImage, fileName);
                if (await DownloadBinaryFile(fileLocation, fileLocationUrlList, url))
                {
                    SetFileDate(fileLocation, postDate);
                    UpdateBlogPostCount(ref counter.Audios, value => blog.DownloadedAudios = value);
                    UpdateBlogProgress(ref counter.TotalDownloads);
                    UpdateBlogDB(fileName);
                }
            }
        }

        private void DownloadText(TumblrPost downloadItem)
        {
            string blogDownloadLocation = blog.DownloadLocation();
            string url = Url(downloadItem);
            string postId = PostId(downloadItem);
            string fileLocation = FileLocationLocalized(blogDownloadLocation, Resources.FileNameTexts);

            if (!CheckIfFileExistsInDB(postId))
            {
                UpdateProgressQueueInformation(Resources.ProgressDownloadImage, postId);
                if (AppendToTextFile(fileLocation, url))
                {
                    UpdateBlogPostCount(ref counter.Audios, value => blog.DownloadedTexts = value);
                    UpdateBlogProgress(ref counter.TotalDownloads);
                    UpdateBlogDB(postId);
                }
            }
        }

        private void DownloadQuote(TumblrPost downloadItem)
        {
            string blogDownloadLocation = blog.DownloadLocation();
            string url = Url(downloadItem);
            string postId = PostId(downloadItem);
            string fileLocation = FileLocationLocalized(blogDownloadLocation, Resources.FileNameQuotes);

            if (!CheckIfFileExistsInDB(postId))
            {
                UpdateProgressQueueInformation(Resources.ProgressDownloadImage, postId);
                if (AppendToTextFile(fileLocation, url))
                {
                    UpdateBlogPostCount(ref counter.Quotes, value => blog.DownloadedQuotes = value);
                    UpdateBlogProgress(ref counter.TotalDownloads);
                    UpdateBlogDB(postId);
                }
            }
        }

        private void DownloadLink(TumblrPost downloadItem)
        {
            string blogDownloadLocation = blog.DownloadLocation();
            string url = Url(downloadItem);
            string postId = PostId(downloadItem);
            string fileLocation = FileLocationLocalized(blogDownloadLocation, Resources.FileNameLinks);

            if (!CheckIfFileExistsInDB(postId))
            {
                UpdateProgressQueueInformation(Resources.ProgressDownloadImage, postId);
                if (AppendToTextFile(fileLocation, url))
                {
                    UpdateBlogPostCount(ref counter.Links, value => blog.DownloadedLinks = value);
                    UpdateBlogProgress(ref counter.TotalDownloads);
                    UpdateBlogDB(postId);
                }
            }
        }

        private void DownloadConversation(TumblrPost downloadItem)
        {
            string blogDownloadLocation = blog.DownloadLocation();
            string url = Url(downloadItem);
            string postId = PostId(downloadItem);
            string fileLocation = FileLocationLocalized(blogDownloadLocation, Resources.FileNameConversations);

            if (!CheckIfFileExistsInDB(postId))
            {
                UpdateProgressQueueInformation(Resources.ProgressDownloadImage, postId);
                if (AppendToTextFile(fileLocation, url))
                {
                    UpdateBlogPostCount(ref counter.Conversations, value => blog.DownloadedConversations = value);
                    UpdateBlogProgress(ref counter.TotalDownloads);
                    UpdateBlogDB(postId);
                }
            }
        }

        private void DownloadAnswer(TumblrPost downloadItem)
        {
            string blogDownloadLocation = blog.DownloadLocation();
            string url = Url(downloadItem);
            string postId = PostId(downloadItem);
            string fileLocation = FileLocationLocalized(blogDownloadLocation, Resources.FileNameAnswers);

            if (!CheckIfFileExistsInDB(postId))
            {
                UpdateProgressQueueInformation(Resources.ProgressDownloadImage, postId);
                if (AppendToTextFile(fileLocation, url))
                {
                    UpdateBlogPostCount(ref counter.Answers, value => blog.DownloadedAnswers = value);
                    UpdateBlogProgress(ref counter.TotalDownloads);
                    UpdateBlogDB(postId);
                }
            }
        }

        private void DownloadPhotoMeta(TumblrPost downloadItem)
        {
            string blogDownloadLocation = blog.DownloadLocation();
            string url = Url(downloadItem);
            string postId = PostId(downloadItem);
            string fileLocation = FileLocationLocalized(blogDownloadLocation, Resources.FileNameMetaPhoto);

            if (!CheckIfFileExistsInDB(postId))
            {
                UpdateProgressQueueInformation(Resources.ProgressDownloadImage, postId);
                if (AppendToTextFile(fileLocation, url))
                {
                    UpdateBlogPostCount(ref counter.PhotoMetas, value => blog.DownloadedPhotoMetas = value);
                    UpdateBlogProgress(ref counter.TotalDownloads);
                    UpdateBlogDB(postId);
                }
            }
        }

        private void DownloadVideoMeta(TumblrPost downloadItem)
        {
            string blogDownloadLocation = blog.DownloadLocation();
            string url = Url(downloadItem);
            string postId = PostId(downloadItem);
            string fileLocation = FileLocationLocalized(blogDownloadLocation, Resources.FileNameMetaVideo);

            if (!CheckIfFileExistsInDB(postId))
            {
                UpdateProgressQueueInformation(Resources.ProgressDownloadImage, postId);
                if (AppendToTextFile(fileLocation, url))
                {
                    UpdateBlogPostCount(ref counter.VideoMetas, value => blog.DownloadedVideoMetas = value);
                    UpdateBlogProgress(ref counter.TotalDownloads);
                    UpdateBlogDB(postId);
                }
            }
        }

        private void DownloadAudioMeta(TumblrPost downloadItem)
        {
            string blogDownloadLocation = blog.DownloadLocation();
            string url = Url(downloadItem);
            string postId = PostId(downloadItem);
            string fileLocation = FileLocationLocalized(blogDownloadLocation, Resources.FileNameMetaAudio);

            if (!CheckIfFileExistsInDB(postId))
            {
                UpdateProgressQueueInformation(Resources.ProgressDownloadImage, postId);
                if (AppendToTextFile(fileLocation, url))
                {
                    UpdateBlogPostCount(ref counter.AudioMetas, value => blog.DownloadedAudioMetas = value);
                    UpdateBlogProgress(ref counter.TotalDownloads);
                    UpdateBlogDB(postId);
                }
            }
        }

        protected void SetFileDate(string fileLocation, DateTime postDate)
        {
            if (!blog.DownloadUrlList)
            {
                File.SetLastWriteTime(fileLocation, postDate);
            }
        }

        protected static string Url(TumblrPost downloadItem)
        {
            return downloadItem.Url;
        }

        private static string FileName(TumblrPost downloadItem)
        {
            return downloadItem.Url.Split('/').Last();
        }

        protected static string FileLocation(string blogDownloadLocation, string fileName)
        {
            return Path.Combine(blogDownloadLocation, fileName);
        }

        protected static string FileLocationLocalized(string blogDownloadLocation, string fileName)
        {
            return Path.Combine(blogDownloadLocation, string.Format(CultureInfo.CurrentCulture, fileName));
        }

        private static string PostId(TumblrPost downloadItem)
        {
            return downloadItem.Id;
        }

        protected static DateTime PostDate(TumblrPost downloadItem)
        {
            if (!string.IsNullOrEmpty(downloadItem.Date))
            {
                var epoch = new DateTime(1970, 1, 1, 0, 0, 0, 0, System.DateTimeKind.Utc);
                DateTime postDate = epoch.AddSeconds(Convert.ToDouble(downloadItem.Date)).ToLocalTime();
                return postDate;
            }
            return DateTime.Now;
        }
    }
}