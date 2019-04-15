﻿namespace BillionSongs {
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using BillionSongs.Data;
    using JetBrains.Annotations;
    using Microsoft.Extensions.Caching.Memory;

    using static System.FormattableString;

    public class CachedSongDatabase: ISongDatabase {
        readonly ILyricsGenerator lyricsGenerator;
        readonly IMemoryCache cache;
        readonly ApplicationDbContext dbContext;

        public CachedSongDatabase(
            [NotNull] ILyricsGenerator lyricsGenerator,
            [NotNull] IMemoryCache cache,
            [NotNull] ApplicationDbContext dbContext) {
            this.lyricsGenerator = lyricsGenerator ?? throw new ArgumentNullException(nameof(lyricsGenerator));
            this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
            this.dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        public async Task<Song> GetSong(uint song, CancellationToken cancellation) {
            while (true) {
                cancellation.ThrowIfCancellationRequested();
                var generationTask = this.cache.GetOrCreate(Invariant($"{this.lyricsGenerator.GetType()}{song}"),
                    cacheEntry => this.GenerateCacheEntry(cacheEntry, song, cancellation));

                try {
                    return await generationTask.ConfigureAwait(false);
                } catch (OperationCanceledException) { }
            }
        }

        Task<Song> GenerateCacheEntry(ICacheEntry cacheEntry, uint song, CancellationToken cancellation) {
            lock (cacheEntry) {
                if (cacheEntry.Value is Task<Song> generating) {
                    return generating;
                } else {
                    cacheEntry.SetSlidingExpiration(TimeSpan.FromHours(24));
                    Task<Song> result = this.FetchOrGenerateSong(song, cancellation);
                    cacheEntry.Value = result;
                    return result;
                }
            }
        }
        async Task<Song> FetchOrGenerateSong(uint songID, CancellationToken cancellation) {
            cancellation.ThrowIfCancellationRequested();

            var song = await this.dbContext.Songs
                .FindAsync(keyValues: new object[] { songID }, cancellation)
                .ConfigureAwait(false);

            if (song != null) return song;

            try {
                string lyrics = await this.lyricsGenerator.GenerateLyrics(songID, cancellation);
                song = new Song {
                    Generated = DateTimeOffset.UtcNow,
                    ID = songID,
                    Lyrics = lyrics,
                };
            } catch(LyricsGeneratorException generatorError) {
                song = new Song {
                    Generated = DateTimeOffset.UtcNow,
                    ID = songID,
                    GeneratorError = generatorError.ToString(),
                };
            }

            this.dbContext.Songs.Add(song);
            await this.dbContext.SaveChangesAsync(CancellationToken.None)
                .ConfigureAwait(false);
            return song;
        }
    }
}
