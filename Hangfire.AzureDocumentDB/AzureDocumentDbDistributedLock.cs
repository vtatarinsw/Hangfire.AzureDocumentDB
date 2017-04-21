﻿using System;
using System.Net;
using System.Linq;

using Microsoft.Azure.Documents;
using Microsoft.Azure.Documents.Client;
using Hangfire.AzureDocumentDB.Entities;

namespace Hangfire.AzureDocumentDB
{
    internal class AzureDocumentDbDistributedLock : IDisposable
    {
        private readonly AzureDocumentDbStorage storage;
        private string selfLink;
        private readonly object syncLock = new object();

        public AzureDocumentDbDistributedLock(string resource, TimeSpan timeout, AzureDocumentDbStorage storage)
        {
            this.storage = storage;
            Acquire(resource, timeout);
        }

        public void Dispose() => Relase();

        private void Acquire(string name, TimeSpan timeout)
        {
            Uri documentCollectionUri = UriFactory.CreateDocumentCollectionUri(storage.Options.DatabaseName, "locks");
            FeedOptions queryOptions = new FeedOptions { MaxItemCount = 1 };

            System.Diagnostics.Stopwatch acquireStart = new System.Diagnostics.Stopwatch();
            acquireStart.Start();

            while (true)
            {
                Lock @lock = storage.Client.CreateDocumentQuery<Lock>(documentCollectionUri, queryOptions)
                                    .Where(l => l.Name == name)
                                    .AsEnumerable()
                                    .FirstOrDefault();

                if (@lock == null)
                {
                    @lock = new Lock { Name = name, ExpireOn = DateTime.UtcNow.Add(timeout) };
                    ResourceResponse<Document> response = storage.Client.CreateDocumentAsync(documentCollectionUri, @lock).GetAwaiter().GetResult();
                    if (response.StatusCode == HttpStatusCode.Created)
                    {
                        selfLink = response.Resource.SelfLink;
                        break;
                    }
                }

                // check the timeout
                if (acquireStart.ElapsedMilliseconds > timeout.TotalMilliseconds)
                {
                    throw new AzureDocumentDbDistributedLockException($"Could not place a lock on the resource '{name}': Lock timeout.");
                }

                // sleep for 500 millisecond
                System.Threading.Thread.Sleep(500);
            }
        }

        private void Relase()
        {
            lock (syncLock)
            {
                storage.Client.DeleteDocumentAsync(selfLink).GetAwaiter().GetResult();
            }
        }
    }
}