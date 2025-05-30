/* ========================================================================
 * Copyright (c) 2005-2021 The OPC Foundation, Inc. All rights reserved.
 *
 * OPC Foundation MIT License 1.00
 *
 * Permission is hereby granted, free of charge, to any person
 * obtaining a copy of this software and associated documentation
 * files (the "Software"), to deal in the Software without
 * restriction, including without limitation the rights to use,
 * copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the
 * Software is furnished to do so, subject to the following
 * conditions:
 *
 * The above copyright notice and this permission notice shall be
 * included in all copies or substantial portions of the Software.
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
 * EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
 * OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
 * NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
 * HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
 * WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
 * FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
 * OTHER DEALINGS IN THE SOFTWARE.
 *
 * The complete license agreement can be found here:
 * http://opcfoundation.org/License/MIT/1.00/
 * ======================================================================*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Json;
using Amazon.S3;
using HotChocolate.AspNetCore;
using HotChocolate.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
#if AZURE_AD
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Logging;
using Microsoft.Identity.Web;
#endif
using Microsoft.OpenApi.Models;
using Opc.Ua.Cloud.Library.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Opc.Ua.Cloud.Library.Authentication;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Net.Sockets;
using System.Threading.Tasks;

[assembly: CLSCompliant(false)]
namespace Opc.Ua.Cloud.Library
{
    public class Startup
    {
        public Startup(IConfiguration configuration, IWebHostEnvironment environment)
        {
            Configuration = configuration;
            Environment = environment;
        }

        public IConfiguration Configuration { get; }

        public IWebHostEnvironment Environment { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddControllersWithViews().AddNewtonsoftJson();

            services.AddRazorPages();

            // Setup database context for ASP.NetCore Identity Scaffolding
            services.AddDbContext<AppDbContext>(
                options => options.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)),
                ServiceLifetime.Transient);

            services.AddDefaultIdentity<IdentityUser>(options =>
                      //require confirmation mail if email sender API Key is set
                      options.SignIn.RequireConfirmedAccount = !string.IsNullOrEmpty(Configuration["EmailSenderAPIKey"])
                    )
                .AddRoles<IdentityRole>()
#if APIKEY_AUTH
                .AddTokenProvider<ApiKeyTokenProvider>(ApiKeyTokenProvider.ApiKeyProviderName)
#endif
                .AddEntityFrameworkStores<AppDbContext>();

            services.AddScoped<IUserService, UserService>();

            services.AddTransient<IDatabase, CloudLibDataProvider>();

            services.AddScoped<ICaptchaValidation, CaptchaValidation>();

            if (!string.IsNullOrEmpty(Configuration["UseSendGridEmailSender"]))
            {
                services.AddTransient<IEmailSender, SendGridEmailSender>();
            }
            else
            {
                services.AddTransient<IEmailSender, PostmarkEmailSender>();
            }

            services.AddLogging(builder => builder.AddConsole());

            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, BasicAuthenticationHandler>("BasicAuthentication", null)
                .AddScheme<AuthenticationSchemeOptions, SignedInUserAuthenticationHandler>("SignedInUserAuthentication", null)
#if APIKEY_AUTH
                .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>("ApiKeyAuthentication", null);
#endif
            ;

            //for captcha validation call
            //add httpclient service for dependency injection
            //https://docs.microsoft.com/en-us/aspnet/core/fundamentals/http-requests?view=aspnetcore-6.0
            services.AddHttpClient();

            if (Configuration["OAuth2ClientId"] != null)
            {
                services.AddAuthentication()
                    .AddOAuth("OAuth", "OPC Foundation", options => {
                        options.AuthorizationEndpoint = "https://opcfoundation.org/oauth/authorize/";
                        options.TokenEndpoint = "https://opcfoundation.org/oauth/token/";
                        options.UserInformationEndpoint = "https://opcfoundation.org/oauth/me";

                        options.AccessDeniedPath = new PathString("/Account/AccessDenied");
                        options.CallbackPath = new PathString("/Account/ExternalLogin");

                        options.ClientId = Configuration["OAuth2ClientId"];
                        options.ClientSecret = Configuration["OAuth2ClientSecret"];

                        options.SaveTokens = true;

                        options.CorrelationCookie.SameSite = SameSiteMode.Strict;
                        options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;

                        options.ClaimActions.MapJsonKey(ClaimTypes.NameIdentifier, "ID");
                        options.ClaimActions.MapJsonKey(ClaimTypes.Name, "display_name");
                        options.ClaimActions.MapJsonKey(ClaimTypes.Email, "user_email");

                        options.Events = new OAuthEvents {
                            OnCreatingTicket = async context => {
                                List<AuthenticationToken> tokens = (List<AuthenticationToken>)context.Properties.GetTokens();

                                tokens.Add(new AuthenticationToken() {
                                    Name = "TicketCreated",
                                    Value = DateTime.UtcNow.ToString(DateTimeFormatInfo.InvariantInfo)
                                });

                                context.Properties.StoreTokens(tokens);

                                HttpResponseMessage response = await context.Backchannel.GetAsync($"{context.Options.UserInformationEndpoint}?access_token={context.AccessToken}").ConfigureAwait(false);
                                response.EnsureSuccessStatusCode();

                                string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                                JsonElement user = JsonDocument.Parse(json).RootElement;

                                context.RunClaimActions(user);
                            }
                        };
                    });
            }

#if AZURE_AD
            if (Configuration.GetSection("AzureAd")?["ClientId"] != null)
            {
                // Web UI access
                services.AddAuthentication()
                    .AddMicrosoftIdentityWebApp(Configuration,
                        configSectionName: "AzureAd",
                        openIdConnectScheme: "AzureAd",
                        displayName: Configuration["AADDisplayName"] ?? "Microsoft Account")
                    ;
                // Allow access to API via Bearer tokens (for service identities etc.)
                services.AddAuthentication()
                    .AddMicrosoftIdentityWebApi(
                        Configuration,
                        configSectionName: "AzureAd",
                        jwtBearerScheme: "Bearer",
                        subscribeToJwtBearerMiddlewareDiagnosticsEvents: true
                        )
                    ;
            }
            else
            {
                // Need to register a Bearer scheme or the authorization attributes cause errors
                services.AddAuthentication()
                    .AddScheme<AuthenticationSchemeOptions, BasicAuthenticationHandler>("Bearer", null);

            }
#if DEBUG
            IdentityModelEventSource.ShowPII = true;
#endif

#endif

            services.AddAuthorization(options => {
                options.AddPolicy("ApprovalPolicy", policy => policy.RequireRole("Administrator"));
                options.AddPolicy("UserAdministrationPolicy", policy => policy.RequireRole("Administrator"));
                options.AddPolicy("DeletePolicy", policy => policy.RequireRole("Administrator"));
            });

            services.AddSwaggerGen(options => {
                options.SwaggerDoc("v1", new OpenApiInfo {
                    Title = "UA Cloud Library REST Service",
                    Version = "v1",
                    Description = "A REST-full interface to the CESMII & OPC Foundation Cloud Library",
                    Contact = new OpenApiContact {
                        Name = "OPC Foundation",
                        Email = "office@opcfoundation.org",
                        Url = new Uri("https://opcfoundation.org/")
                    }
                });

                options.AddSecurityDefinition("basicAuth", new OpenApiSecurityScheme {
                    Type = SecuritySchemeType.Http,
                    Scheme = "basic"
                });

                options.AddSecurityRequirement(new OpenApiSecurityRequirement
                {
                    {
                          new OpenApiSecurityScheme
                            {
                                Reference = new OpenApiReference
                                {
                                    Type = ReferenceType.SecurityScheme,
                                    Id = "basicAuth"
                                }
                            },
                            Array.Empty<string>()
                    }
                });

#if APIKEY_AUTH
                options.AddSecurityDefinition("ApiKeyAuth", new OpenApiSecurityScheme {
                    Type = SecuritySchemeType.ApiKey,
                    In = ParameterLocation.Header,
                    Name = "X-API-Key",
                    //Scheme = "basic"
                });

                options.AddSecurityRequirement(new OpenApiSecurityRequirement
            {
                    {
                          new OpenApiSecurityScheme
                            {
                                Reference = new OpenApiReference
                                {
                                    Type = ReferenceType.SecurityScheme,
                                    Id = "ApiKeyAuth"
                                }
                            },
                            Array.Empty<string>()
                    }
                });
#endif

                options.CustomSchemaIds(type => type.ToString());

                options.EnableAnnotations();
            });

            services.AddSwaggerGenNewtonsoftSupport();

            // Setup file storage
            switch (Configuration["HostingPlatform"])
            {
                case "Azure": services.AddSingleton<IFileStorage, AzureFileStorage>(); break;
                case "AWS":
                    Amazon.Extensions.NETCore.Setup.AWSOptions awsOptions = Configuration.GetAWSOptions();
                    services.AddDefaultAWSOptions(awsOptions);
                    services.AddAWSService<IAmazonS3>();
                    services.AddSingleton<IFileStorage, AWSFileStorage>();
                    break;
                case "GCP": services.AddSingleton<IFileStorage, GCPFileStorage>(); break;
                case "DevDB": services.AddScoped<IFileStorage, DevDbFileStorage>(); break;
                default:
                {
                    services.AddSingleton<IFileStorage, LocalFileStorage>();
                    Console.WriteLine("WARNING: Using local filesystem for storage as HostingPlatform environment variable not specified or invalid!");
                    break;
                }
            }

            string serviceName = Configuration["Application"] ?? "UACloudLibrary";

            // setup data protection
            switch (Configuration["HostingPlatform"])
            {
                case "Azure": services.AddDataProtection().PersistKeysToAzureBlobStorage(Configuration["BlobStorageConnectionString"], "keys", Configuration["DataProtectionBlobName"]); break;
                case "AWS": services.AddDataProtection().PersistKeysToAWSSystemsManager($"/{serviceName}/DataProtection"); break;
                case "GCP": services.AddDataProtection().PersistKeysToGoogleCloudStorage(Configuration["BlobStorageConnectionString"], "DataProtectionProviderKeys.xml"); break;
                default: services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Directory.GetCurrentDirectory())); break;
            }

            services.AddHttpContextAccessor();


            HotChocolate.Types.Pagination.PagingOptions paginationConfig;
            IConfigurationSection section = Configuration.GetSection("GraphQLPagination");
            if (section.Exists())
            {
                paginationConfig = section.Get<HotChocolate.Types.Pagination.PagingOptions>();
            }
            else
            {
                paginationConfig = new HotChocolate.Types.Pagination.PagingOptions {
                    IncludeTotalCount = true,
                    DefaultPageSize = 100,
                    MaxPageSize = 100,
                };
            }

            services.AddGraphQLServer()
                .AddAuthorization()
                .ModifyPagingOptions(o => {
                    o.IncludeTotalCount = paginationConfig.IncludeTotalCount;
                    o.DefaultPageSize = paginationConfig.DefaultPageSize;
                    o.MaxPageSize = paginationConfig.MaxPageSize;
                })
                .AddFiltering(fd => {
                    fd.AddDefaults().BindRuntimeType<UInt32, UnsignedIntOperationFilterInputType>();
                    fd.AddDefaults().BindRuntimeType<UInt32?, UnsignedIntOperationFilterInputType>();
                    fd.AddDefaults().BindRuntimeType<UInt16?, UnsignedShortOperationFilterInputType>();
                })
                .AddSorting()
                .AddQueryType<QueryModel>()
                .AddMutationType<MutationModel>()
                .AddType<CloudLibNodeSetModelType>()
                .BindRuntimeType<UInt32, HotChocolate.Types.UnsignedIntType>()
                .BindRuntimeType<UInt16, HotChocolate.Types.UnsignedShortType>()
                .ModifyCostOptions(options =>
                {
                    options.MaxFieldCost = 1_000;
                    options.MaxTypeCost = 1_000;
                    options.EnforceCostLimits = false;
                    options.ApplyCostDefaults = false;
                    options.DefaultResolverCost = 10.0;
                });

            services.AddScoped<NodeSetModelIndexer>();
            services.AddScoped<NodeSetModelIndexerFactory>();

            services.Configure<IISServerOptions>(options => {
                options.AllowSynchronousIO = true;
            });

            services.Configure<KestrelServerOptions>(options => {
                options.AllowSynchronousIO = true;
            });

            services.AddServerSideBlazor();
#if AZURE_AD
            // Required to make Azure AD login work as ASP.Net External Identity: Change the SignInScheme to External after ALL other configuration have run.
            services
              .AddOptions()
              .PostConfigureAll<OpenIdConnectOptions>(o => {
                  o.SignInScheme = IdentityConstants.ExternalScheme;
              });
#endif
        }


        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, AppDbContext appDbContext)
        {
            uint retryCount = 0;
            while (retryCount < 12)
            {
                try
                {
                    appDbContext.Database.Migrate();
                    break;
                }
                catch (SocketException)
                {
                    Console.WriteLine("Database not yet available or unknown, retrying...");
                    Task.Delay(5000).GetAwaiter().GetResult();
                    retryCount++;
                }
            }

            if (retryCount == 12)
            {
                // database permanently unavailable
                throw new InvalidOperationException("Database not available, exiting!");
            }

            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseSwagger();

            app.UseSwaggerUI(c => {
                c.SwaggerEndpoint("/swagger/v1/swagger.json", "UA Cloud Library REST Service");
            });

            app.UseHttpsRedirection();

            app.UseStaticFiles();

            app.UseRouting();

            app.UseAuthentication();

            app.UseAuthorization();

            app.UseGraphQLGraphiQL("/graphiql", new GraphQL.Server.Ui.GraphiQL.GraphiQLOptions {
                RequestCredentials = GraphQL.Server.Ui.GraphiQL.RequestCredentials.Include,
            });

            app.UseEndpoints(endpoints => {
                endpoints.MapControllerRoute(
                    name: "default",
                    pattern: "{controller=Home}/{action=Index}/{id?}");

                endpoints.MapRazorPages();
                endpoints.MapBlazorHub();
                endpoints.MapGraphQL()
                    .RequireAuthorization(new AuthorizeAttribute() { AuthenticationSchemes = UserService.APIAuthorizationSchemes })
                    .WithOptions(new GraphQLServerOptions {
                        EnableGetRequests = true,
                        Tool = { Enable = false },
                    });
            });
        }
    }
}
