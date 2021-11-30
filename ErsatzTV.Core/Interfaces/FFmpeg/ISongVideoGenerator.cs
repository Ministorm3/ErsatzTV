﻿using System;
using System.Threading.Tasks;
using ErsatzTV.Core.Domain;
using LanguageExt;

namespace ErsatzTV.Core.Interfaces.FFmpeg
{
    public interface ISongVideoGenerator
    {
        Task<Tuple<string, MediaVersion>> GenerateSongVideo(
            Song song,
            Channel channel,
            Option<ChannelWatermark> maybeGlobalWatermark,
            string ffmpegPath);
    }
}