// <auto-generated>
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.
//
// </auto-generated>

namespace Microsoft.DotNet.Maestro.Client.Models
{
    using Newtonsoft.Json;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;

    public partial class Channel
    {
        /// <summary>
        /// Initializes a new instance of the Channel class.
        /// </summary>
        public Channel()
        {
            CustomInit();
        }

        /// <summary>
        /// Initializes a new instance of the Channel class.
        /// </summary>
        public Channel(int? id = default(int?), string name = default(string), string classification = default(string), IList<ReleasePipeline> releasePipelines = default(IList<ReleasePipeline>))
        {
            Id = id;
            Name = name;
            Classification = classification;
            ReleasePipelines = releasePipelines;
            CustomInit();
        }

        /// <summary>
        /// An initialization method that performs custom operations like setting defaults
        /// </summary>
        partial void CustomInit();

        /// <summary>
        /// </summary>
        [JsonProperty(PropertyName = "id")]
        public int? Id { get; set; }

        /// <summary>
        /// </summary>
        [JsonProperty(PropertyName = "name")]
        public string Name { get; set; }

        /// <summary>
        /// </summary>
        [JsonProperty(PropertyName = "classification")]
        public string Classification { get; set; }

        /// <summary>
        /// </summary>
        [JsonProperty(PropertyName = "releasePipelines")]
        public IList<ReleasePipeline> ReleasePipelines { get; set; }

    }
}
