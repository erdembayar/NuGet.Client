// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace NuGet.Shared
{
    internal static class XmlUtility
    {
        /// <summary>
        /// Creates a new System.Xml.Linq.XDocument from a file.
        /// </summary>
        /// <param name="inputUri">A URI string that references the file to load into a new <see cref="System.Xml.Linq.XDocument"/></param>
        /// <returns>An <see cref="System.Xml.Linq.XDocument"/> that contains the contents of the specified file.</returns>
        internal static XDocument Load(string inputUri)
        {
            using (var reader = XmlReader.Create(inputUri, GetXmlReaderSettings()))
            {
                return XDocument.Load(reader);
            }
        }

        /// <summary>
        /// Creates a new System.Xml.Linq.XDocument from a stream.
        /// </summary>
        /// <param name="input">The stream that contains the XML data.</param>
        /// <returns>An <see cref="System.Xml.Linq.XDocument"/> that contains the contents of the specified stream.</returns>
        internal static XDocument Load(Stream input)
        {
            using (var reader = XmlReader.Create(input, GetXmlReaderSettings()))
            {
                return XDocument.Load(reader);
            }
        }

        /// <summary>
        /// Creates an instance of System.Xml.XmlReaderSettings with safe settings
        /// </summary>
        private static XmlReaderSettings GetXmlReaderSettings()
        {
            return new XmlReaderSettings()
            {
                IgnoreWhitespace = true,
                IgnoreProcessingInstructions = true,
                IgnoreComments = true
            };
        }
    }
}
