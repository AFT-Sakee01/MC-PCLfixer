using System.Net;
using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;
using PclRepair;

// Private signing material exists only in this test process. The fake HTTPS
// handler exercises the production verifier without publishing a trusted feed
// or adding an insecure transport exception to the delivered application.
public static class FeedTests
{
    static int count;

    sealed class Handler : HttpMessageHandler
    {
        public int Requests;
        public Func<HttpRequestMessage,HttpResponseMessage> Respond = _ => throw new HttpRequestException("Test offline");
        public Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>>? RespondAsync;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            return RespondAsync==null?Task.FromResult(Respond(request)):RespondAsync(request,cancellationToken);
        }
    }

    sealed class UnknownLengthStream : MemoryStream
    {
        public UnknownLengthStream(byte[] bytes) : base(bytes) { }
        public override bool CanSeek => false;
    }

    static void Check(bool value,string name)
    {
        if(!value) throw new Exception("Feed check failed: "+name);
        Console.WriteLine("PASS "+name);
        count++;
    }

    static void Refuses(Action action,string name)
    {
        bool refused=false;
        try { action(); } catch(Exception) { refused=true; }
        Check(refused,name);
    }

    static T Copy<T>(T value) => Json.Decode<T>(Json.Encode(value));

    static byte[] Mutate(byte[] bytes,Action<ModuleDefinition> change)
    {
        using(var module=Modules.Read(bytes))
        using(var output=new MemoryStream())
        {
            change(module);
            module.Write(output);
            return output.ToArray();
        }
    }

    static RecipeCatalog Catalog(RecipeProfile profile,long sequence=10) => new RecipeCatalog {
        schema_version=1,
        sequence=sequence,
        issued_utc=DateTime.UtcNow.AddMinutes(-1).ToString("o"),
        expires_utc=DateTime.UtcNow.AddDays(7).ToString("o"),
        min_tool_version=Program.Version,
        profiles=new List<RecipeProfile>{profile}
    };

    static byte[] Sign(RecipeCatalog catalog,RSA key)
    {
        byte[] payload=Json.Encode(catalog);
        return Json.Encode(new SignedRecipeFeed {
            schema_version=1,
            algorithm="RSA-PSS-SHA256",
            payload=Convert.ToBase64String(payload),
            signature=Convert.ToBase64String(key.SignData(payload,HashAlgorithmName.SHA256,RSASignaturePadding.Pss))
        });
    }

    static HttpResponseMessage Response(byte[] bytes,HttpStatusCode status=HttpStatusCode.OK) =>
        new HttpResponseMessage(status){Content=new ByteArrayContent(bytes)};

    static string Cache(string root) => Directory.GetFiles(root,"feed.json",SearchOption.AllDirectories).Single();

    public static int Run(byte[] original,string root)
    {
        count=0;
        root=Path.Combine(root,"feed-tests");
        Directory.CreateDirectory(root);
        var embedded=Repair.LoadProfile();
        // This future fixture changes BOTH the download method and updater
        // initializer. The old embedded pair must reject it; merely fetching
        // the unchanged embedded recipe would not demonstrate adaptation.
        byte[] future=Mutate(original,module=>{
            var symbols=new Symbols(module);
            symbols.Definitions[embedded.Download.methods[0].name].Body.Instructions.Insert(0,Instruction.Create(OpCodes.Nop));
            symbols.Definitions[UpdateCompatibility.MethodName].Body.Instructions.Insert(0,Instruction.Create(OpCodes.Nop));
            module.Assembly.Name.Version=new Version(2,99,2,0);
        });
        var updated=Copy(embedded);
        using(var module=Modules.Read(future))
        {
            var symbols=new Symbols(module);
            updated.Download.methods[0].before_hash=Bodies.Fingerprint(symbols.Definitions[updated.Download.methods[0].name]);
            updated.Download.restricted_sha256=Hash.Bytes(future);
            var initializer=symbols.Definitions[UpdateCompatibility.MethodName];
            updated.Update.before_hash=Bodies.Fingerprint(initializer);
            var assignment=initializer.Body.Instructions.Single(i=>i.OpCode==OpCodes.Stsfld
                &&i.Operand is FieldReference f&&f.FullName==UpdateCompatibility.FieldName);
            assignment.Previous.Operand=updated.Update.replacement_value;
            updated.Update.after_hash=Bodies.Fingerprint(initializer);
        }
        Refuses(()=>Repair.Inspect(future,embedded),"embedded pair refuses changed future download and updater bodies");
        Repair.ValidateProfile(updated);
        byte[] expected=Repair.Transform(future,updated);
        Check(Repair.Inspect(expected,updated).State=="repaired"
            &&Repair.Inspect(expected,updated).FullUpdates,"reviewed external pair repairs both future structures");
        Check(Repair.Transform(expected,updated).SequenceEqual(expected),"external pair repair is idempotent");

        using(var key=RSA.Create(3072))
        using(var otherKey=RSA.Create(3072))
        {
            string pem=key.ExportSubjectPublicKeyInfoPem();
            byte[] valid=Sign(Catalog(updated),key);
            RecipeFeedWire.Verify(valid,pem);
            Check(true,"RSA-PSS signed recipe catalog accepted");
            Refuses(()=>RecipeFeedWire.Verify(valid,otherKey.ExportSubjectPublicKeyInfoPem()),"different trusted public key rejects signed catalog");
            var tampered=Json.Decode<SignedRecipeFeed>(valid);
            byte[] changedPayload=Convert.FromBase64String(tampered.payload);
            changedPayload[changedPayload.Length-2]^=1;
            tampered.payload=Convert.ToBase64String(changedPayload);
            Refuses(()=>RecipeFeedWire.Verify(Json.Encode(tampered),pem),"payload modification invalidates signature");
            tampered=Json.Decode<SignedRecipeFeed>(valid);
            byte[] changedSignature=Convert.FromBase64String(tampered.signature);
            changedSignature[0]^=1;
            tampered.signature=Convert.ToBase64String(changedSignature);
            Refuses(()=>RecipeFeedWire.Verify(Json.Encode(tampered),pem),"signature modification rejected");
            tampered=Json.Decode<SignedRecipeFeed>(valid);
            tampered.algorithm="RSA-PKCS1-SHA256";
            Refuses(()=>RecipeFeedWire.Verify(Json.Encode(tampered),pem),"unapproved signature algorithm rejected");
            tampered=Json.Decode<SignedRecipeFeed>(valid);
            tampered.payload="not base64";
            Refuses(()=>RecipeFeedWire.Verify(Json.Encode(tampered),pem),"malformed feed envelope rejected");
            var badCatalog=Catalog(updated);
            badCatalog.schema_version=2;
            Refuses(()=>RecipeFeedWire.Verify(Sign(badCatalog,key),pem),"unsupported catalog schema rejected");
            badCatalog=Catalog(updated);
            badCatalog.min_tool_version="999.0.0.0";
            Refuses(()=>RecipeFeedWire.Verify(Sign(badCatalog,key),pem),"catalog requiring a newer helper rejected");
            badCatalog=Catalog(updated);
            badCatalog.expires_utc=DateTime.UtcNow.AddDays(-1).ToString("o");
            badCatalog.issued_utc=DateTime.UtcNow.AddDays(-2).ToString("o");
            byte[] expired=Sign(badCatalog,key);
            Refuses(()=>RecipeFeedWire.Verify(expired,pem),"expired signed catalog cannot authorize repair");
            RecipeFeedWire.Verify(expired,pem,false);
            Check(true,"expired authentic catalog remains usable only as rollback metadata");
            badCatalog=Catalog(updated);
            badCatalog.profiles=new List<RecipeProfile>();
            Refuses(()=>RecipeFeedWire.Verify(Sign(badCatalog,key),pem),"empty signed catalog rejected");
            var invalidProfile=Copy(updated);
            invalidProfile.Download.methods[0].name=UpdateCompatibility.MethodName;
            Refuses(()=>RecipeFeedWire.Verify(Sign(Catalog(invalidProfile),key),pem),"signed recipe cannot expand the fixed method scope");
            invalidProfile=Copy(updated);
            invalidProfile.Download.methods[1].name=invalidProfile.Download.methods[0].name;
            Refuses(()=>RecipeFeedWire.Verify(Sign(Catalog(invalidProfile),key),pem),"duplicate target method rejected");
            invalidProfile=Copy(updated);
            invalidProfile.Download.methods[0].after_hash=new string('0',64);
            Refuses(()=>RecipeFeedWire.Verify(Sign(Catalog(invalidProfile),key),pem),"replacement body must match its declared fingerprint");
            invalidProfile=Copy(updated);
            invalidProfile.Download.guards.Clear();
            Refuses(()=>{
                var verified=RecipeFeedWire.Verify(Sign(Catalog(invalidProfile),key),pem);
                Repair.Inspect(future,verified.profiles[0]);
            },"remote recipe cannot remove guards for required local dependencies");
            invalidProfile=Copy(updated);
            invalidProfile.Update.field_name="System.String PCL.ModSecret::OtherField";
            Refuses(()=>RecipeFeedWire.Verify(Sign(Catalog(invalidProfile),key),pem),"signed update recipe cannot modify another field");
            invalidProfile=Copy(updated);
            invalidProfile.Update.replacement_value="EmptyReenableDelta";
            Refuses(()=>RecipeFeedWire.Verify(Sign(Catalog(invalidProfile),key),pem),"signed update recipe cannot change the approved full-update marker");

            string configured=Path.Combine(root,"configured");
            var transport=new Handler{Respond=_=>Response(valid)};
            var client=new RecipeFeeds(configured,null,transport);
            Refuses(()=>client.Configure("http://recipes.example.test/feed.json",pem),"unencrypted recipe source refused");
            Refuses(()=>client.Configure("https://recipes.example.test/feed.json","not a public key"),"invalid trust key refused");
            client.Configure("https://recipes.example.test/feed.json",pem);
            Check(File.Exists(Path.Combine(configured,"recipe-source.json")),"explicit HTTPS source and public key saved");
            client.Select(original);
            Check(transport.Requests==0,"compatible embedded recipe never requires network");
            string target=Path.Combine(configured,"PCL.exe");
            File.WriteAllBytes(target,future);
            var inspection=client.Apply(target);
            Check(inspection.State=="repaired"&&inspection.FullUpdates&&Hash.File(target)==Hash.Bytes(expected),
                "incompatible future launcher downloads signed profile and repairs");
            Check(transport.Requests==1,"one network request obtains the complete signed profile pair");
            Check(File.Exists(Cache(configured)),"verified signed feed cached atomically");
            Check(Repair.Restore(target)==Hash.Bytes(future)&&File.ReadAllBytes(target).SequenceEqual(future),
                "remote-profile repair restores the exact future input");
            var offline=new Handler();
            var cachedClient=new RecipeFeeds(configured,null,offline);
            cachedClient.Select(future,false);
            cachedClient.Select(future);
            Check(offline.Requests==0,"matching signed cache works offline and suppresses network");
            string cacheFile=Cache(configured);
            byte[] pristineCache=File.ReadAllBytes(cacheFile);
            File.WriteAllBytes(cacheFile,Encoding.UTF8.GetBytes("{\"schema_version\":1,\"payload\":\"tampered\"}"));
            Refuses(()=>cachedClient.Select(future),"tampered cache rejected instead of resetting rollback floor");
            Check(offline.Requests==0,"damaged cache cannot silently fall back to a fresh network trust decision");
            File.WriteAllBytes(cacheFile,pristineCache);
            client.Disable();
            Check(client.Status().StartsWith("DISABLED ",StringComparison.Ordinal),"disable records an inactive source without losing its trust identity");
            var disabledTransport=new Handler{Respond=_=>Response(valid)};
            Refuses(()=>new RecipeFeeds(configured,null,disabledTransport).Select(future),"disabled source stops automatic remote repair");
            Check(disabledTransport.Requests==0,"disabled source makes no network requests");

            // Seed a valid high sequence with an unmatched profile. Even a
            // catalog without a match must advance the source's signed floor.
            string rollbackRoot=Path.Combine(root,"rollback");
            var rollbackTransport=new Handler{Respond=_=>Response(Sign(Catalog(embedded,20),key))};
            var rollbackClient=new RecipeFeeds(rollbackRoot,null,rollbackTransport);
            rollbackClient.Configure("https://recipes.example.test/rollback.json",pem);
            Refuses(()=>rollbackClient.Select(future),"valid catalog without a matching profile refuses modification");
            string rollbackFile=Cache(rollbackRoot);
            string baseline=Hash.File(rollbackFile);
            rollbackTransport.Respond=_=>Response(Sign(Catalog(updated,19),key));
            Refuses(()=>rollbackClient.Select(future),"older signed sequence cannot replace a newer cached catalog");
            Check(Hash.File(rollbackFile)==baseline,"rollback rejection preserves the cached trust floor");
            rollbackTransport.Respond=_=>Response(Sign(Catalog(updated,20),key));
            Refuses(()=>rollbackClient.Select(future),"same sequence with a different signed payload rejected");
            Check(Hash.File(rollbackFile)==baseline,"equivocation rejection preserves the accepted catalog");
            var oldCatalog=Catalog(updated,20);
            oldCatalog.issued_utc=DateTime.UtcNow.AddDays(-2).ToString("o");
            oldCatalog.expires_utc=DateTime.UtcNow.AddDays(-1).ToString("o");
            File.WriteAllBytes(rollbackFile,Sign(oldCatalog,key));
            rollbackTransport.Respond=_=>Response(Sign(Catalog(updated,19),key));
            Refuses(()=>rollbackClient.Select(future),"expired authentic cache still prevents sequence rollback");
            rollbackTransport.Respond=_=>Response(Sign(Catalog(updated,21),key));
            var refreshed=rollbackClient.Select(future);
            Check(Repair.Inspect(Repair.Transform(future,refreshed),refreshed).State=="repaired",
                "expired matching cache refreshes to a newer signed catalog");

            string concurrentRoot=Path.Combine(root,"concurrent");
            var concurrentTransport=new Handler{Respond=_=>{Thread.Sleep(200);return Response(valid);}};
            new RecipeFeeds(concurrentRoot,null,concurrentTransport).Configure("https://recipes.example.test/concurrent.json",pem);
            var concurrentErrors=new System.Collections.Concurrent.ConcurrentQueue<Exception>();
            Parallel.For(0,3,_=>{
                try {new RecipeFeeds(concurrentRoot,null,concurrentTransport).Select(future);}
                catch(Exception ex) {concurrentErrors.Enqueue(ex);}
            });
            Check(concurrentErrors.IsEmpty&&concurrentTransport.Requests==1,"concurrent empty-cache selectors download once and reuse the committed feed");
            RecipeFeedWire.Verify(File.ReadAllBytes(Cache(concurrentRoot)),pem);
            Check(true,"concurrent refresh leaves one complete authenticated cache");

            string failureRoot=Path.Combine(root,"failure");
            var failing=new Handler();
            var failureClient=new RecipeFeeds(failureRoot,null,failing);
            failureClient.Configure("https://recipes.example.test/failure.json",pem);
            string failureTarget=Path.Combine(failureRoot,"PCL.exe");
            File.WriteAllBytes(failureTarget,future);
            Refuses(()=>failureClient.Apply(failureTarget),"network failure refuses an incompatible repair");
            Check(Hash.File(failureTarget)==Hash.Bytes(future),"network failure leaves launcher byte-identical");
            failing.Respond=_=>Response(Sign(Catalog(updated),otherKey));
            Refuses(()=>failureClient.Apply(failureTarget),"download signed by an untrusted publisher rejected");
            Check(Hash.File(failureTarget)==Hash.Bytes(future),"untrusted feed leaves launcher byte-identical");
            failing.Respond=_=>Response(expired);
            Refuses(()=>failureClient.Apply(failureTarget),"downloaded expired profile refused");
            failing.Respond=_=>Response(new byte[8*1024*1024+1]);
            Refuses(()=>failureClient.Apply(failureTarget),"oversized recipe download refused");
            failing.Respond=_=>new HttpResponseMessage(HttpStatusCode.OK){
                Content=new StreamContent(new UnknownLengthStream(new byte[8*1024*1024+1]))
            };
            Refuses(()=>failureClient.Apply(failureTarget),"oversized streamed response refused without Content-Length");
            failing.Respond=_=>{
                var response=Response(Array.Empty<byte>(),HttpStatusCode.Found);
                response.Headers.Location=new Uri("https://other.example.test/feed.json");
                return response;
            };
            Refuses(()=>failureClient.Apply(failureTarget),"HTTP redirect is not followed");
            failing.Respond=_=>Response(Array.Empty<byte>(),HttpStatusCode.ServiceUnavailable);
            Refuses(()=>failureClient.Apply(failureTarget),"HTTP failure is not treated as a recipe");
            failing.RespondAsync=async (_,cancellationToken)=>{
                await Task.Delay(Timeout.Infinite,cancellationToken);
                return Response(valid);
            };
            var deadline=System.Diagnostics.Stopwatch.StartNew();
            Refuses(()=>failureClient.Apply(failureTarget),"stalled network request canceled by bounded deadline");
            Check(deadline.Elapsed<TimeSpan.FromSeconds(25),"network cancellation returns without unbounded retry");
            Check(Hash.File(failureTarget)==Hash.Bytes(future)
                &&!Directory.Exists(Path.Combine(failureRoot,".pcl-repair","backups")),"failed feed checks create no launcher backup or mutation");

            // A launcher updater can replace the EXE while HTTPS is in flight.
            // Even if that replacement happens to match the selected recipe,
            // the helper must reselect it instead of modifying unobserved bytes.
            string raceRoot=Path.Combine(root,"target-race");
            string raceTarget=Path.Combine(raceRoot,"PCL.exe");
            byte[] replacement=Mutate(future,m=>m.Assembly.Name.Version=new Version(2,99,3,0));
            var raceTransport=new Handler{Respond=_=>{
                File.WriteAllBytes(raceTarget,replacement);
                return Response(valid);
            }};
            var raceClient=new RecipeFeeds(raceRoot,null,raceTransport);
            raceClient.Configure("https://recipes.example.test/target-race.json",pem);
            File.WriteAllBytes(raceTarget,future);
            Refuses(()=>raceClient.Apply(raceTarget),"launcher replacement during recipe download is not patched using a stale selection");
            Check(Hash.File(raceTarget)==Hash.Bytes(replacement)
                &&!Directory.Exists(Path.Combine(raceRoot,".pcl-repair","backups")),"selection race preserves replacement bytes and creates no launcher backup");

            // A separate real-CLR handoff test consumes only public fixtures.
            // The ephemeral publisher private key is never written to disk.
            string handoff=Path.Combine(root,"direct-handoff");
            Directory.CreateDirectory(handoff);
            File.WriteAllBytes(Path.Combine(handoff,"future.exe"),future);
            File.WriteAllText(Path.Combine(handoff,"expected.sha256"),Hash.Bytes(expected));
            File.WriteAllText(Path.Combine(handoff,"publisher-public.pem"),pem);
            File.WriteAllBytes(Path.Combine(handoff,"feed.json"),valid);
        }
        Console.WriteLine("ALL "+count+" FEED CHECKS PASSED");
        return count;
    }
}
