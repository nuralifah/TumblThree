﻿using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Waf.Foundation;
using System.Windows.Input;
using TumblThree.Applications.Services;
using TumblThree.Domain.Models;
using TumblThree.Domain.Queue;

namespace TumblThree.Presentation.DesignData
{
    public class MockCrawlerService : Model, ICrawlerService
    {
        private readonly ObservableCollection<QueueListItem> activeItems;
        private readonly ReadOnlyObservableList<QueueListItem> readonlyActiveItems;

        public MockCrawlerService()
        {
            this.activeItems = new ObservableCollection<QueueListItem>();
            this.readonlyActiveItems = new ReadOnlyObservableList<QueueListItem>(activeItems);
        }

        public IReadOnlyObservableList<QueueListItem> ActiveItems { get { return readonlyActiveItems; } }

        public void SetActiveBlogFiles(IEnumerable<IBlog> blogFilesToAdd)
        {
            activeItems.Clear();
            blogFilesToAdd.ToList().ForEach(x => activeItems.Add(new QueueListItem(x)));
        }

        public void AddActiveItems(QueueListItem itemToAdd)
        {

        }

        public void RemoveActiveItem(QueueListItem itemToRemove)
        {

        }

        public ICommand RemoveBlogCommand { get; set; }

        public ICommand AddBlogCommand { get; set; }

        public ICommand AddBlogToQueueCommand { get; set; }

        public ICommand RemoveBlogFromQueueCommand { get; set; }

        public ICommand ShowFilesCommand { get; set; }

        public ICommand EnqueueSelectedCommand { get; set; }

        public ICommand ListenClipboardCommand { get; set; }

        public ICommand CrawlCommand { get; set; }

        public ICommand PauseCommand { get; set; }

        public ICommand ResumeCommand { get; set; }

        public ICommand StopCommand { get; set; }

        public ICommand AutoDownloadCommand { get; set; }

        public bool IsCrawl { get; set; }

        public bool IsPaused { get; set; }

        public string NewBlogUrl { get; set; }

        public Guava.RateLimiter.RateLimiter Timeconstraint { get; set; }
    }
}