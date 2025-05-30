using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Opc.Ua.Cloud.Library;
using Opc.Ua.Cloud.Library.Client;
using Opc.Ua.Export;
using Xunit;
using Xunit.Abstractions;

namespace CloudLibClient.Tests
{
    [Collection("Run")]
    public class QueriesAndDownload
    : IClassFixture<CustomWebApplicationFactory<Opc.Ua.Cloud.Library.Startup>>
    {
        private readonly CustomWebApplicationFactory<Opc.Ua.Cloud.Library.Startup> _factory;
        private readonly ITestOutputHelper output;

        public QueriesAndDownload(CustomWebApplicationFactory<Opc.Ua.Cloud.Library.Startup> factory, ITestOutputHelper output)
        {
            _factory = factory;
            this.output = output;
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task GetNodeSetDependencies(bool forceRest)
        {
            UACloudLibClient client = _factory.CreateCloudLibClient();
            if (forceRest)
            {
                client._forceRestTestHook = forceRest;
                client._allowRestFallback = true;
            }

            UANodesetResult nodeSetInfo = await GetBasicNodeSetInfoForNamespaceAsync(client, strTestNamespaceUri).ConfigureAwait(true);

            Assert.True(nodeSetInfo != null, "Nodeset not found");
            string identifier = nodeSetInfo.Id.ToString(CultureInfo.InvariantCulture);

            List<Nodeset> nodeSetsById = await client.GetNodeSetDependencies(identifier: identifier).ConfigureAwait(true);

            Assert.True(nodeSetsById?.Count == 1);
            Nodeset nodeSet = nodeSetsById[0];

            UANameSpace uploadedNamespace = GetUploadedTestNamespace();

            Assert.Equal(uploadedNamespace.Nodeset.NamespaceUri?.OriginalString, nodeSet.NamespaceUri?.OriginalString);
            Assert.Equal(uploadedNamespace.Nodeset.PublicationDate, nodeSet.PublicationDate);
            Assert.Equal(uploadedNamespace.Nodeset.Version, nodeSet.Version);
            Assert.Equal(nodeSetInfo.Id, nodeSet.Identifier);
            Assert.Equal("INDEXED", nodeSet.ValidationStatus, ignoreCase: true);
            Assert.Equal(default, nodeSet.LastModifiedDate);
            Assert.True(string.IsNullOrEmpty(nodeSet.NodesetXml));

            Console.WriteLine($"Dependencies for {nodeSet.Identifier} {nodeSet.NamespaceUri} {nodeSet.PublicationDate} ({nodeSet.Version}):");
            foreach (RequiredModelInfo requiredNodeSet in nodeSet.RequiredModels)
            {
                Console.WriteLine($"Required: {requiredNodeSet.NamespaceUri} {requiredNodeSet.PublicationDate} ({requiredNodeSet.Version}). Available in Cloud Library: {requiredNodeSet.AvailableModel?.Identifier} {requiredNodeSet.AvailableModel?.PublicationDate} ({requiredNodeSet.AvailableModel?.Version})");
            }

            VerifyRequiredModels(uploadedNamespace, nodeSet.RequiredModels);

            string namespaceUri = nodeSetInfo.NameSpaceUri;
            DateTime? publicationDate = nodeSetInfo.PublicationDate.HasValue && nodeSetInfo.PublicationDate.Value.Kind == DateTimeKind.Unspecified ?
                DateTime.SpecifyKind(nodeSetInfo.PublicationDate.Value, DateTimeKind.Utc)
                : nodeSetInfo.PublicationDate;

            List<Nodeset> nodeSetsByNamespace = await client.GetNodeSetDependencies(modelUri: namespaceUri, publicationDate: publicationDate).ConfigureAwait(true);

            var dependenciesByNamespace = nodeSetsByNamespace
                .SelectMany(n => n.RequiredModels).Where(r => r != null)
                .Select(r => (r.AvailableModel?.Identifier, r.NamespaceUri, r.PublicationDate))
                .OrderBy(m => m.Identifier).ThenBy(m => m.NamespaceUri).Distinct()
                .ToList();
            var dependenciesByIdentifier = nodeSetsById
                .SelectMany(n => n.RequiredModels).Where(r => r != null)
                .Select(r => (r.AvailableModel?.Identifier, r.NamespaceUri, r.PublicationDate))
                .OrderBy(m => m.Identifier).ThenBy(m => m.NamespaceUri).Distinct()
                .ToList();
            if (!dependenciesByIdentifier.SequenceEqual(dependenciesByNamespace))
            {
                Console.WriteLine($"FAIL: returned dependencies are different.");
                Console.WriteLine($"For identifier {identifier}: {string.Join(" ", dependenciesByIdentifier)}.");
                Console.WriteLine($"For namespace {namespaceUri} / {publicationDate}: {string.Join(" ", dependenciesByNamespace)}");
                Assert.Fail("Returned dependencies are different. See log for details.");
            }
            else
            {
                Console.WriteLine("Passed.");
            }
        }

        private static async Task<UANodesetResult> GetBasicNodeSetInfoForNamespaceAsync(UACloudLibClient client, string namespaceUri)
        {
            int offset = 0;
            int limit = 10;
            List<UANodesetResult> restResult;
            UANodesetResult nodeSetInfo;
            do
            {
                restResult = await client.GetBasicNodesetInformationAsync(offset, limit).ConfigureAwait(false);
                Assert.True(offset > 0 || restResult?.Count > 0, "Failed to get node set information.");
                nodeSetInfo = restResult.FirstOrDefault(n => n.NameSpaceUri == namespaceUri);
                offset += limit;
            } while (nodeSetInfo == null && restResult.Count == limit);
            return nodeSetInfo;
        }

        private static UANodeSet VerifyRequiredModels(UANameSpace expectedNamespace, List<RequiredModelInfo> requiredModels)
        {
            UANodeSet uaNodeSet = null;
            if (expectedNamespace != null)
            {
                using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(expectedNamespace.Nodeset.NodesetXml)))
                {
                    uaNodeSet = UANodeSet.Read(ms);
                }
            }
            VerifyRequiredModels(uaNodeSet, requiredModels);
            return uaNodeSet;
        }

        private static void VerifyRequiredModels(UANodeSet expectedUaNodeSet, List<RequiredModelInfo> requiredModels)
        {
            if (expectedUaNodeSet == null && requiredModels == null)
            {
                return;
            }
            List<RequiredModelInfo> expectedModels;
            expectedModels = expectedUaNodeSet?.Models.SelectMany(m => m.RequiredModel).Select(rm =>
                        new RequiredModelInfo {
                            NamespaceUri = rm.ModelUri,
                            PublicationDate = rm.PublicationDate,
                            Version = rm.Version,
                            // TODO verify AvailableModel
                        }).ToList();

            Assert.Equal(expectedModels?.OrderBy(m => m.NamespaceUri), requiredModels?.OrderBy(m => m.NamespaceUri), new RequiredModelInfoComparer());
        }

        [Fact]
        public async Task DownloadNodesetAsync()
        {
            UACloudLibClient client = _factory.CreateCloudLibClient();

            GraphQlResult<Nodeset> nodeSetsResult = await client.GetNodeSetsAsync(modelUri: strTestNamespaceUri).ConfigureAwait(true);
            Assert.True(nodeSetsResult.TotalCount > 0, "Failed to download node set info");
            Nodeset testNodeSet = nodeSetsResult.Nodes.FirstOrDefault(r => r.NamespaceUri.OriginalString == strTestNamespaceUri);

            UANameSpace downloadedNamespace = await client.DownloadNodesetAsync(testNodeSet.Identifier).ConfigureAwait(true);

            Assert.NotNull(downloadedNamespace);
            Assert.Equal(downloadedNamespace.Nodeset.NamespaceUri.OriginalString, testNodeSet.NamespaceUri.OriginalString);
            Assert.False(string.IsNullOrEmpty(downloadedNamespace?.Nodeset?.NodesetXml), "No nodeset XML returned");

            UANameSpace uploadedNamespace = GetUploadedTestNamespace();

            Assert.Equal(uploadedNamespace.Nodeset.NodesetXml, downloadedNamespace.Nodeset.NodesetXml);

            uint identifier = downloadedNamespace.Nodeset.Identifier;
            Assert.True(identifier == testNodeSet.Identifier);

            Assert.Equal("INDEXED", downloadedNamespace.Nodeset.ValidationStatus, ignoreCase: true);

            UANodeSet uploadedUaNodeSet = VerifyRequiredModels(uploadedNamespace, downloadedNamespace.Nodeset.RequiredModels);
            Assert.Equal(uploadedUaNodeSet.LastModified, downloadedNamespace.Nodeset.LastModifiedDate);
            Assert.Equal(uploadedUaNodeSet.Models[0].ModelUri, downloadedNamespace.Nodeset.NamespaceUri.OriginalString);
            Assert.Equal(uploadedUaNodeSet.Models[0].PublicationDate, downloadedNamespace.Nodeset.PublicationDate);
            Assert.Equal(uploadedUaNodeSet.Models[0].Version, downloadedNamespace.Nodeset.Version);

            Assert.Equal(uploadedNamespace.Nodeset.NamespaceUri?.OriginalString, downloadedNamespace.Nodeset.NamespaceUri.OriginalString);
            Assert.Equal(uploadedNamespace.Nodeset.PublicationDate, downloadedNamespace.Nodeset.PublicationDate);
            Assert.Equal(uploadedNamespace.Nodeset.Version, downloadedNamespace.Nodeset.Version);
            Assert.Equal(uploadedNamespace.Nodeset.LastModifiedDate, downloadedNamespace.Nodeset.LastModifiedDate);

            Assert.Equal(uploadedNamespace.Title, downloadedNamespace.Title);
            Assert.Equal(uploadedNamespace.License, downloadedNamespace.License);
            Assert.Equal(uploadedNamespace.Keywords, downloadedNamespace.Keywords);
            Assert.Equal(uploadedNamespace.LicenseUrl, downloadedNamespace.LicenseUrl);
            Assert.Equal(uploadedNamespace.TestSpecificationUrl, downloadedNamespace.TestSpecificationUrl);
            Assert.Equal(uploadedNamespace.Category, downloadedNamespace.Category, new CategoryComparer());
            Assert.Equal(uploadedNamespace.Contributor, downloadedNamespace.Contributor, new OrganisationComparer());

            Assert.Equal(uploadedNamespace.AdditionalProperties, downloadedNamespace.AdditionalProperties, new UAPropertyComparer());

            Assert.Equal(uploadedNamespace.CopyrightText, downloadedNamespace.CopyrightText);
            Assert.Equal(uploadedNamespace.Description, downloadedNamespace.Description);
            Assert.Equal(uploadedNamespace.DocumentationUrl, downloadedNamespace.DocumentationUrl);
            Assert.True(downloadedNamespace.CreationTime != null && DateTime.Now - downloadedNamespace.CreationTime < new TimeSpan(1, 0, 0));
            Assert.Equal(uploadedNamespace.IconUrl, downloadedNamespace.IconUrl);
            Assert.Equal(uploadedNamespace.PurchasingInformationUrl, downloadedNamespace.PurchasingInformationUrl);
            Assert.Equal(uploadedNamespace.ReleaseNotesUrl, downloadedNamespace.ReleaseNotesUrl);
            Assert.Equal(uploadedNamespace.SupportedLocales, downloadedNamespace.SupportedLocales);
        }

        const string strTestNamespaceUri = "http://cloudlibtests/testnodeset001/";
        const string strTestNamespaceTitle = "CloudLib Test Nodeset 001";
        const string strTestNamespaceFilename = "cloudlibtests.testnodeset001.NodeSet2.xml.0.json";
        const string strTestNamespaceUpdateFilename = "cloudlibtests.testnodeset001.V1_2.NodeSet2.xml.0.json";
        const string strTestDependingNamespaceFilename = "cloudlibtests.dependingtestnodeset001.V1_2.NodeSet2.xml.0.json";
        private static UANameSpace GetUploadedTestNamespace()
        {
            string uploadedJson = File.ReadAllText(Path.Combine("TestNamespaces", strTestNamespaceFilename));
            UANameSpace uploadedNamespace = JsonConvert.DeserializeObject<UANameSpace>(uploadedJson);
            return uploadedNamespace;
        }

        [Fact]
        public async Task GetBasicNodesetInformationAsync()
        {
            UACloudLibClient client = _factory.CreateCloudLibClient();

            UANodesetResult basicNodesetInfo = await GetBasicNodeSetInfoForNamespaceAsync(client, strTestNamespaceUri).ConfigureAwait(true);
            Assert.True(basicNodesetInfo != null, $"Test Nodeset {strTestNamespaceUri} not found");
            Assert.True(basicNodesetInfo.Id != 0);

            UANameSpace uploadedNamespace = GetUploadedTestNamespace();
            Assert.Equal(uploadedNamespace.Nodeset.NamespaceUri?.OriginalString, basicNodesetInfo.NameSpaceUri);
            Assert.Equal(uploadedNamespace.Nodeset.PublicationDate, basicNodesetInfo.PublicationDate);
            Assert.Equal(uploadedNamespace.Nodeset.Version, basicNodesetInfo.Version);
            Assert.Equal(uploadedNamespace.License.ToString(), basicNodesetInfo.License);
            Assert.Equal(uploadedNamespace.Title, basicNodesetInfo.Title);
            Assert.Equal(uploadedNamespace.Contributor.Name, basicNodesetInfo.Contributor);
            VerifyRequiredModels(uploadedNamespace, basicNodesetInfo.RequiredNodesets);
        }

        [Fact]
        public async Task GetNamespaceIdsAsync()
        {
            UACloudLibClient client = _factory.CreateCloudLibClient();

            (string NamespaceUri, string Identifier)[] restResult = await client.GetNamespaceIdsAsync().ConfigureAwait(true);
            Assert.True(restResult?.Length > 0, "Failed to download namespace ids");
            (string NamespaceUri, string Identifier) testNodeSet = restResult.FirstOrDefault(r => r.NamespaceUri == strTestNamespaceUri);
            Assert.NotNull(testNodeSet.NamespaceUri);
            Assert.NotNull(testNodeSet.Identifier);
        }

        [Theory]
        [InlineData(true, true, true, true)]
        [InlineData(false, true, true, false)]
        [InlineData(false, false, false, false)]
        [InlineData(true, false, false, false)]
        [InlineData(false, true, false, false)]
        [InlineData(false, false, true, false)]

        public async Task GetNodeSetsAsync(bool noMetadata, bool noRequiredModels, bool noTotalCount, bool noCreationTime)
        {
            UACloudLibClient client = _factory.CreateCloudLibClient();
            string cursor = null;
            int limit = 10;
            Nodeset testNodeSet;
            bool moreData;
            int? totalCount = null;
            do
            {
                GraphQlResult<Nodeset> result = await client.GetNodeSetsAsync(after: cursor, first: limit, noRequiredModels: noRequiredModels, noMetadata: noMetadata, noTotalCount: noTotalCount, noCreationTime: noCreationTime).ConfigureAwait(true);
                Assert.True(cursor == null || result.Edges?.Count > 0, "Failed to get node set information.");

                testNodeSet = result.Edges.FirstOrDefault(n => n.Node.NamespaceUri.OriginalString == strTestNamespaceUri)?.Node;
                if (testNodeSet != null && result.TotalCount > 0)
                {
                    totalCount = result.TotalCount;
                }
                cursor = result.PageInfo.EndCursor;
                moreData = result.PageInfo.HasNextPage;
            } while (testNodeSet == null && moreData);
            Assert.True(testNodeSet != null, "Nodeset not found");

            Assert.True(testNodeSet.Identifier != 0);
            Assert.Equal(strTestNamespaceUri, testNodeSet.NamespaceUri?.OriginalString);

            UANameSpace uploadedNamespace = GetUploadedTestNamespace();

            Assert.Equal(uploadedNamespace.Nodeset.NamespaceUri?.OriginalString, testNodeSet.NamespaceUri.OriginalString);
            Assert.Equal(uploadedNamespace.Nodeset.PublicationDate, testNodeSet.PublicationDate);
            Assert.Equal(uploadedNamespace.Nodeset.Version, testNodeSet.Version);
            if (noMetadata)
            {
                Assert.Null(testNodeSet.Metadata);
            }
            else
            {
                Assert.Equal(uploadedNamespace.Title, testNodeSet.Metadata.Title);
                Assert.Equal(uploadedNamespace.License, testNodeSet.Metadata.License);
                Assert.Equal(uploadedNamespace.Keywords, testNodeSet.Metadata.Keywords);
                Assert.Equal(uploadedNamespace.LicenseUrl, testNodeSet.Metadata.LicenseUrl);
                Assert.Equal(uploadedNamespace.TestSpecificationUrl, testNodeSet.Metadata.TestSpecificationUrl);
                Assert.Equal(uploadedNamespace.Category, testNodeSet.Metadata.Category, new CategoryComparer());

                Assert.Equal(default/*uploadedNamespace.Nodeset.LastModifiedDate*/, testNodeSet.LastModifiedDate); // TODO
                Assert.Equal(uploadedNamespace.Contributor, testNodeSet.Metadata.Contributor, new OrganisationComparer());

                Assert.Equal(uploadedNamespace.AdditionalProperties.OrderBy(p => p.Name), testNodeSet.Metadata.AdditionalProperties.OrderBy(p => p.Name), new UAPropertyComparer());
                Assert.Equal(uploadedNamespace.CopyrightText, testNodeSet.Metadata.CopyrightText);
                Assert.Equal(uploadedNamespace.Description, testNodeSet.Metadata.Description);
                Assert.Equal(uploadedNamespace.DocumentationUrl, testNodeSet.Metadata.DocumentationUrl);
                if (noCreationTime)
                {
                    Assert.Null(testNodeSet.Metadata.CreationTime);
                }
                else
                {
                    Assert.True(testNodeSet.Metadata.CreationTime != null && DateTime.Now - testNodeSet.Metadata.CreationTime < new TimeSpan(1, 0, 0));
                }
                Assert.Equal(uploadedNamespace.IconUrl, testNodeSet.Metadata.IconUrl);
                Assert.Equal(uploadedNamespace.PurchasingInformationUrl, testNodeSet.Metadata.PurchasingInformationUrl);
                Assert.Equal(uploadedNamespace.ReleaseNotesUrl, testNodeSet.Metadata.ReleaseNotesUrl);
                Assert.Equal(uploadedNamespace.SupportedLocales, testNodeSet.Metadata.SupportedLocales);
            }
            if (noRequiredModels)
            {
                Assert.True(testNodeSet.RequiredModels?.Count == 0);
            }
            else
            {
                VerifyRequiredModels(uploadedNamespace/*(UANameSpace) null*/, testNodeSet.RequiredModels);
            }

            if (noTotalCount)
            {
                Assert.Null(totalCount);
            }
            else
            {
                Assert.True(totalCount > 60);
            }
            Assert.True(string.IsNullOrEmpty(testNodeSet.NodesetXml));
            Assert.Equal("INDEXED", testNodeSet.ValidationStatus);
        }

        [Theory]
        [InlineData(new[] { "plastic", "robot", "machine" }, 34)]
        [InlineData(new[] { "plastic" }, 15)]
        [InlineData(new[] { "robot"}, 4)]
        [InlineData(new[] { "machine" }, 33)]
        public async Task GetNodeSetsFilteredAsync(string[] keywords, int expectedCount)
        {
            UACloudLibClient client = _factory.CreateCloudLibClient();

            GraphQlResult<Nodeset> result = await client.GetNodeSetsAsync(keywords: keywords).ConfigureAwait(true);
            Assert.Equal(expectedCount, result.TotalCount);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task GetConvertedMetadataAsync(bool forceRest)
        {
            UACloudLibClient client = _factory.CreateCloudLibClient();
            client._forceRestTestHook = forceRest;
            client._allowRestFallback = true; // GraphQL support was deprecated and is removed now: allow fallback to REST until the client uses new GraphQL mechanisms.

            List<UANameSpace> restResult = await client.GetConvertedMetadataAsync(0, 10).ConfigureAwait(true);

            Assert.True(restResult?.Count > 0, "Failed to get node set information.");

            UANameSpace convertedMetaData = restResult.FirstOrDefault(n => n.Nodeset.NamespaceUri?.OriginalString == strTestNamespaceUri);
            if (convertedMetaData == null)
            {
                convertedMetaData = restResult.FirstOrDefault(n => n.Title == strTestNamespaceTitle || string.Equals(n.Category.Name, strTestNamespaceTitle, StringComparison.OrdinalIgnoreCase));
            }

            Assert.True(convertedMetaData != null, $"Test Nodeset {strTestNamespaceUri} not found");
            Assert.True(string.IsNullOrEmpty(convertedMetaData.Nodeset.NodesetXml));
            Assert.True(convertedMetaData.Nodeset.Identifier != 0);

            UANameSpace uploadedNamespace = GetUploadedTestNamespace();
            Assert.Null(convertedMetaData.Nodeset.NamespaceUri);
            Assert.Equal(uploadedNamespace.Nodeset.PublicationDate, convertedMetaData.Nodeset.PublicationDate);
            Assert.Equal(uploadedNamespace.Nodeset.Version, convertedMetaData.Nodeset.Version);

            Assert.Equal(uploadedNamespace.Title, convertedMetaData.Title);
            Assert.Equal(uploadedNamespace.License, convertedMetaData.License);
            Assert.Equal(uploadedNamespace.Keywords, convertedMetaData.Keywords);
            Assert.Equal(uploadedNamespace.LicenseUrl, convertedMetaData.LicenseUrl);
            Assert.Equal(uploadedNamespace.TestSpecificationUrl, convertedMetaData.TestSpecificationUrl);
            Assert.Equal(uploadedNamespace.Category, convertedMetaData.Category, new CategoryComparer());
            Assert.Equal(default, convertedMetaData.Nodeset.LastModifiedDate); // REST does not return last modified date
            Assert.Equal(uploadedNamespace.Contributor?.Name, convertedMetaData.Contributor?.Name);
            Assert.Equal(uploadedNamespace.AdditionalProperties.OrderBy(p => p.Name), convertedMetaData.AdditionalProperties.OrderBy(p => p.Name), new UAPropertyComparer());
            Assert.Equal(uploadedNamespace.CopyrightText, convertedMetaData.CopyrightText);
            Assert.Equal(uploadedNamespace.Description, convertedMetaData.Description);
            Assert.Equal(uploadedNamespace.DocumentationUrl, convertedMetaData.DocumentationUrl);
            Assert.Null(uploadedNamespace.CreationTime);
            Assert.Equal(uploadedNamespace.IconUrl, convertedMetaData.IconUrl);
            Assert.Equal(uploadedNamespace.PurchasingInformationUrl, convertedMetaData.PurchasingInformationUrl);
            Assert.Equal(uploadedNamespace.ReleaseNotesUrl, convertedMetaData.ReleaseNotesUrl);
            Assert.Equal(uploadedNamespace.SupportedLocales, convertedMetaData.SupportedLocales);
            VerifyRequiredModels((UANameSpace)null, convertedMetaData.Nodeset.RequiredModels);
        }

        [Theory]
        [InlineData("OtherTestNamespaces", strTestNamespaceUpdateFilename)]
        [InlineData("TestNamespaces", strTestNamespaceFilename, true)]
        [InlineData("TestNamespaces", "opcfoundation.org.UA.DI.NodeSet2.xml.2844662655.json", true)]
        [InlineData("TestNamespaces", "opcfoundation.org.UA.2022-11-01.NodeSet2.xml.3338611482.json", true)]
        [InlineData("OtherTestNamespaces", strTestDependingNamespaceFilename, false, strTestNamespaceUpdateFilename)] // Depends on test namespace 1.02
        public async Task UpdateNodeSet(string path, string fileName, bool uploadConflictExpected = false, string dependentNodeSet = null)
        {
            UACloudLibClient client = _factory.CreateCloudLibClient();

            int expectedNodeSetCount = (await client.GetNodeSetsAsync().ConfigureAwait(true)).TotalCount;

            string uploadedIdentifier = null;
            string uploadJson = File.ReadAllText(Path.Combine(path, fileName));
            UANameSpace addressSpace = JsonConvert.DeserializeObject<UANameSpace>(uploadJson);
            (HttpStatusCode Status, string Message) response = await client.UploadNodeSetAsync(addressSpace).ConfigureAwait(true);
            if (response.Status == HttpStatusCode.OK)
            {
                output.WriteLine($"Uploaded {addressSpace?.Nodeset.NamespaceUri}, {addressSpace?.Nodeset.Identifier}");
                uploadedIdentifier = response.Message;
                UANameSpace approvalResult = await client.UpdateApprovalStatusAsync(uploadedIdentifier, "APPROVED", null, null).ConfigureAwait(true);
                Assert.NotNull(approvalResult);
                Assert.Equal("APPROVED", approvalResult.ApprovalStatus);
            }
            else
            {
                if (response.Status == HttpStatusCode.Conflict || response.Message.Contains("Nodeset already exists", StringComparison.OrdinalIgnoreCase))
                {
                    Assert.True(uploadConflictExpected || _factory.TestConfig.IgnoreUploadConflict,
                            $"Error uploading {addressSpace?.Nodeset.NamespaceUri}, {addressSpace?.Nodeset.Identifier}: {response.Status} {response.Message}");
                    if (!uploadConflictExpected)
                    {
                        output.WriteLine($"Namespace {addressSpace?.Nodeset.NamespaceUri}, {addressSpace?.Nodeset.Identifier} already exists. Ignored due to TestConfig.IgnoreUploadConflict == true");
                    }
                }
                else
                {
                    Assert.Equal(HttpStatusCode.OK, response.Status);
                    uploadedIdentifier = response.Message;
                }
            }
            // Upload again should cause conflict
            response = await client.UploadNodeSetAsync(addressSpace).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Conflict, response.Status);

            GraphQlResult<Nodeset> nodeSetInfo;
            // Wait for indexing

            bool notIndexed;
            bool dependencyUploaded = false;
            string requiredIdentifier = null;
            do
            {
                nodeSetInfo = await client.GetNodeSetsAsync(modelUri: addressSpace.Nodeset.NamespaceUri.OriginalString, publicationDate: addressSpace.Nodeset.PublicationDate).ConfigureAwait(true);
                Assert.NotEmpty(nodeSetInfo.Nodes);
                Nodeset uploadedNode = nodeSetInfo.Nodes.Where(n => n.NamespaceUri.OriginalString == addressSpace.Nodeset.NamespaceUri.OriginalString && n.PublicationDate == addressSpace.Nodeset.PublicationDate).FirstOrDefault();
                Assert.Contains(uploadedNode, nodeSetInfo.Nodes);
                if (dependentNodeSet != null && !dependencyUploaded)
                {
                    if (uploadedNode.ValidationStatus == "PARSED")
                    {
                        await Task.Delay(5000);
                        notIndexed = true;
                    }
                    else
                    {
                        // Verify that the dependency is missing
                        Assert.Equal("ERROR", uploadedNode.ValidationStatus);

                        string requiredUploadJson = File.ReadAllText(Path.Combine(path, dependentNodeSet));
                        UANameSpace requiredAddressSpace = JsonConvert.DeserializeObject<UANameSpace>(requiredUploadJson);
                        response = await client.UploadNodeSetAsync(requiredAddressSpace).ConfigureAwait(true);
                        Assert.Equal(HttpStatusCode.OK, response.Status);
                        requiredIdentifier = response.Message;

                        UANameSpace approvalResult = await client.UpdateApprovalStatusAsync(requiredIdentifier, "APPROVED", null, null);
                        Assert.NotNull(approvalResult);
                        Assert.Equal("APPROVED", approvalResult.ApprovalStatus);

                        dependencyUploaded = true;
                        notIndexed = true;
                    }
                }
                else
                {
                    //Assert.NotEqual("ERROR", uploadedNode.ValidationStatus);
                    notIndexed = uploadedNode.ValidationStatus != "INDEXED";
                    if (notIndexed)
                    {
                        await Task.Delay(5000);
                    }
                }
            } while (notIndexed);
            await UploadAndIndex.WaitForIndexAsync(_factory.CreateAuthorizedClient(), expectedNodeSetCount).ConfigureAwait(true);

            // Upload with override
            response = await client.UploadNodeSetAsync(addressSpace, true).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, response.Status);
            {
                uploadedIdentifier = response.Message;
                UANameSpace approvalResult = await client.UpdateApprovalStatusAsync(uploadedIdentifier, "APPROVED", null, null).ConfigureAwait(true);
                Assert.NotNull(approvalResult);
                Assert.Equal("APPROVED", approvalResult.ApprovalStatus);
            }
            // Wait for indexing
            do
            {
                nodeSetInfo = await client.GetNodeSetsAsync(modelUri: addressSpace.Nodeset.NamespaceUri.OriginalString, publicationDate: addressSpace.Nodeset.PublicationDate).ConfigureAwait(true);
                notIndexed = nodeSetInfo.TotalCount == 1 && nodeSetInfo.Edges[0].Node.ValidationStatus != "INDEXED";
                if (notIndexed)
                {
                    await Task.Delay(5000);
                }
            } while (notIndexed);
            await UploadAndIndex.WaitForIndexAsync(_factory.CreateAuthorizedClient(), expectedNodeSetCount);
            if (!uploadConflictExpected && uploadedIdentifier != null)
            {
                UANameSpace cancelResult = await client.UpdateApprovalStatusAsync(uploadedIdentifier, "CANCELED", "Test cleanup", null);
                Assert.NotNull(cancelResult);
                Assert.Equal("CANCELED", cancelResult.ApprovalStatus);
            }
            if (requiredIdentifier != null)
            {
                UANameSpace cancelResult = await client.UpdateApprovalStatusAsync(requiredIdentifier, "CANCELED", "Test cleanup", null);
                Assert.NotNull(cancelResult);
                Assert.Equal("CANCELED", cancelResult.ApprovalStatus);
            }

            //Trigger reindexing
            addressSpace.Nodeset.NodesetXml = null;
            await client.UploadNodeSetAsync(addressSpace, false);

            await UploadAndIndex.WaitForIndexAsync(_factory.CreateAuthorizedClient(), expectedNodeSetCount).ConfigureAwait(true);
        }
    }
}
