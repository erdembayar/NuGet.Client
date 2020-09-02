// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NuGet.PackageManagement.VisualStudio;
using NuGet.VisualStudio.Internal.Contracts;

namespace NuGet.PackageManagement.UI
{
    internal interface IItemLoaderState
    {
        LoadingStatus LoadingStatus { get; }
        int ItemsCount { get; }
        IDictionary<string, LoadingStatus> SourceLoadingStatus { get; }
        Guid? OperationId { get; }
    }

    /// <summary>
    /// Represents stateful item loader contract that supports pagination and background loading
    /// </summary>
    /// <typeparam name="T"></typeparam>
    internal interface IItemLoader<T>
    {
        bool IsMultiSource { get; }

        IItemLoaderState State { get; }

        IEnumerable<T> GetCurrent();

        Task LoadNextAsync(IProgress<IItemLoaderState> progress, CancellationToken cancellationToken);

        Task UpdateStateAsync(IProgress<IItemLoaderState> progress, CancellationToken cancellationToken);

        void Reset();

        Task<int> GetTotalCountAsync(int maxCount, CancellationToken cancellationToken);
    }
}
