﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Runtime.Serialization;
using Newtonsoft.Json;

#nullable enable

namespace BuildXL.Cache.Host.Configuration
{
    [DataContract]
    public class AzureBlobStorageLogPublicConfiguration
    {
        /// <nodoc />
        [DataMember]
        public string? SecretName { get; set; }

        [DataMember]
        public bool UseSasTokens { get; set; } = false;

        /// <nodoc />
        [DataMember]
        public string? WorkspaceFolderPath { get; set; }

        /// <nodoc />
        [DataMember]
        public string? ContainerName { get; set; }

        /// <nodoc />
        [DataMember]
        public int? WriteMaxDegreeOfParallelism { get; set; }

        /// <nodoc />
        [DataMember]
        public int? WriteMaxIntervalSeconds { get; set; }

        /// <nodoc />
        [DataMember]
        public int? WriteMaxBatchSize { get; set; }

        /// <nodoc />
        [DataMember]
        public int? UploadMaxDegreeOfParallelism { get; set; }

        /// <nodoc />
        [DataMember]
        public int? UploadMaxIntervalSeconds { get; set; }

        [JsonConstructor]
        public AzureBlobStorageLogPublicConfiguration()
        {
        }
    }
}
