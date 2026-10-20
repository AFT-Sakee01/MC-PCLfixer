using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using PclRepair;

static class FeedPublisher
{
    const int MaxCatalogBytes=4*1024*1024;
    const string Algorithm="RSA-PSS-SHA256";

    static int Main(string[] args)
    {
        try
        {
            if(args.Length==2&&args[0]=="template") Template(args[1]);
            else if(args.Length==3&&args[0]=="verify") Verify(args[1],args[2]);
            else
            {
                if(!OperatingSystem.IsWindows())
                    throw new PlatformNotSupportedException("Persistent signing keys require Windows CNG.");
                if(args.Length==3&&args[0]=="keygen") Keygen(args[1],args[2]);
                else if(args.Length==4&&args[0]=="sign") Sign(args[1],args[2],args[3]);
                else if(args.Length==1&&args[0]=="self-test") SelfTest();
                else
                {
                    Console.WriteLine("template <new-catalog.json>");
                    Console.WriteLine("verify <feed.json> <public.pem>");
                    Console.WriteLine("keygen <CNG-key-name> <new-public.pem> (Windows CurrentUser)");
                    Console.WriteLine("sign <CNG-key-name> <catalog.json> <new-feed.json> (Windows)");
                    Console.WriteLine("self-test (Windows; creates and deletes only its own temporary CNG key)");
                    return 2;
                }
            }
            return 0;
        }
        catch(Exception ex)
        {
            // No raw exception, catalog, key name, user path or key material enters logs.
            Console.Error.WriteLine("Recipe feed operation refused: "+ex.GetType().Name);
            return 2;
        }
    }

    static RecipeCatalog NewCatalog()
    {
        var now=DateTime.UtcNow;
        return new RecipeCatalog {
            schema_version=1,
            sequence=1,
            issued_utc=now.ToString("O",CultureInfo.InvariantCulture),
            expires_utc=now.AddDays(30).ToString("O",CultureInfo.InvariantCulture),
            min_tool_version="1.0.0.2",
            profiles=new List<RecipeProfile>{Repair.LoadProfile()}
        };
    }

    static void Template(string output)
    {
        var catalog=NewCatalog();
        RecipeFeedWire.ValidateCatalog(catalog);
        WriteNew(CheckedPath(output,".json"),Json.Encode(catalog));
        Console.WriteLine("Unsigned reviewed-profile template created; review sequence, dates and profiles before signing.");
    }

    static void CheckKeyName(string name)
    {
        if(String.IsNullOrEmpty(name)||name.Length>128)
            throw new InvalidOperationException("Invalid signing key name.");
        foreach(char c in name)
            if(!((c>='a'&&c<='z')||(c>='A'&&c<='Z')||(c>='0'&&c<='9')
                ||c=='.'||c=='-'||c=='_'))
                throw new InvalidOperationException("Invalid signing key name.");
    }

    [SupportedOSPlatform("windows")]
    static CngKey CreateKey(string name)
    {
        CheckKeyName(name);
        // No MachineKey or OverwriteExistingKey flags: scope stays with this user,
        // and a concurrent/existing publisher key can never be overwritten.
        var parameters=new CngKeyCreationParameters {
            Provider=CngProvider.MicrosoftSoftwareKeyStorageProvider,
            KeyCreationOptions=CngKeyCreationOptions.None,
            ExportPolicy=CngExportPolicies.None,
            KeyUsage=CngKeyUsages.Signing
        };
        parameters.Parameters.Add(new CngProperty("Length",BitConverter.GetBytes(3072),CngPropertyOptions.None));
        return CngKey.Create(CngAlgorithm.Rsa,name,parameters);
    }

    [SupportedOSPlatform("windows")]
    static void CheckKey(CngKey key)
    {
        if(key.Algorithm!=CngAlgorithm.Rsa||key.IsMachineKey
            ||key.ExportPolicy!=CngExportPolicies.None
            ||(key.KeyUsage&CngKeyUsages.Signing)==0||key.KeySize!=3072)
            throw new InvalidOperationException("Signing key does not match the publisher policy.");
    }

    [SupportedOSPlatform("windows")]
    static void Keygen(string name,string output)
    {
        string path=CheckedPath(output,".pem");
        if(File.Exists(path)) throw new IOException("Output already exists.");
        using(var key=CreateKey(name))
        {
            try
            {
                CheckKey(key);
                using(var rsa=new RSACng(key))
                    WriteNew(path,new UTF8Encoding(false).GetBytes(rsa.ExportSubjectPublicKeyInfoPem()+"\n"));
            }
            catch
            {
                // Only this invocation's newly created key is removed if publishing
                // its public half failed; no existing system key is opened here.
                key.Delete();
                throw;
            }
        }
        Console.WriteLine("RSA-3072 signing key created in Windows CurrentUser CNG; only its public PEM was written.");
    }

    static byte[] ReadCatalog(string path)
    {
        byte[] bytes=ReadBounded(path,".json",MaxCatalogBytes);
        // Signing covers these exact UTF-8 bytes, with no reserialization or
        // newline normalization between review, signature and verification.
        new UTF8Encoding(false,true).GetString(bytes);
        RecipeFeedWire.ValidateCatalog(Json.Decode<RecipeCatalog>(bytes));
        return bytes;
    }

    static byte[] ReadBounded(string path,string extension,int limit)
    {
        path=CheckedPath(path,extension);
        using(var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            if(input.Length<=0||input.Length>limit)
                throw new InvalidOperationException("Input size is outside publisher limits.");
            var bytes=new byte[checked((int)input.Length)];
            input.ReadExactly(bytes);
            return bytes;
        }
    }

    static void Verify(string input,string publicKey)
    {
        byte[] envelope=ReadBounded(input,".json",RecipeFeedWire.MaxBytes);
        string pem=new UTF8Encoding(false,true).GetString(ReadBounded(publicKey,".pem",16384));
        var catalog=RecipeFeedWire.Verify(envelope,pem);
        // Print only verified, canonical public metadata; paths, raw input and
        // signature/key contents are intentionally absent from the summary.
        Console.WriteLine("Verified recipe feed: sequence="+catalog.sequence.ToString(CultureInfo.InvariantCulture)
            +", profiles="+catalog.profiles.Count.ToString(CultureInfo.InvariantCulture)
            +", min_tool_version="+Version.Parse(catalog.min_tool_version).ToString()+".");
    }

    static byte[] Envelope(byte[] payload,RSA rsa)
    {
        var signature=rsa.SignData(payload,HashAlgorithmName.SHA256,RSASignaturePadding.Pss);
        return Json.Encode(new SignedRecipeFeed {
            schema_version=1,
            algorithm=Algorithm,
            payload=Convert.ToBase64String(payload),
            signature=Convert.ToBase64String(signature)
        });
    }

    [SupportedOSPlatform("windows")]
    static void Sign(string name,string input,string output)
    {
        CheckKeyName(name);
        string path=CheckedPath(output,".json");
        if(File.Exists(path)) throw new IOException("Output already exists.");
        byte[] payload=ReadCatalog(input);
        using(var key=CngKey.Open(name,CngProvider.MicrosoftSoftwareKeyStorageProvider,CngKeyOpenOptions.None))
        {
            CheckKey(key);
            using(var rsa=new RSACng(key))
            {
                byte[] envelope=Envelope(payload,rsa);
                RecipeFeedWire.Verify(envelope,rsa.ExportSubjectPublicKeyInfoPem());
                WriteNew(path,envelope);
            }
        }
        Console.WriteLine("Signed recipe feed created and verified; no hosting or remote publication was performed.");
    }

    static string CheckedPath(string path,string extension)
    {
        string full=Path.GetFullPath(path);
        if(!String.Equals(Path.GetExtension(full),extension,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected file extension.");
        string parent=Path.GetDirectoryName(full);
        if(!Directory.Exists(parent)) throw new DirectoryNotFoundException();
        // The immutable archive/backup rule also applies to this maintenance tool.
        // Reject reparse ancestors so a seemingly new output cannot escape through
        // a symlink into a protected or unrelated directory.
        for(string current=parent;current!=null;current=Path.GetDirectoryName(current))
        {
            if(String.Equals(Path.GetFileName(current),"Backups",StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Protected directory.");
            if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)
                throw new InvalidOperationException("Reparse path is not supported.");
        }
        if(File.Exists(full)&&(File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0)
            throw new InvalidOperationException("Reparse file is not supported.");
        return full;
    }

    static void WriteNew(string path,byte[] bytes)
    {
        string temporary=Path.Combine(Path.GetDirectoryName(path),".feed-publisher-"+Guid.NewGuid().ToString("N")+".tmp");
        bool created=false;
        try
        {
            using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {
                created=true;
                output.Write(bytes);
                output.Flush(true);
            }
            // A sibling staging file prevents partial public keys/feeds; Move
            // without overwrite also closes the exists-check race with other writers.
            File.Move(temporary,path,false);
            created=false;
        }
        finally
        {
            if(created) File.Delete(temporary);
        }
    }

    [SupportedOSPlatform("windows")]
    static void SelfTest()
    {
        string name="PCL-Recipe-Feed-SelfTest-"+Guid.NewGuid().ToString("N");
        using(var key=CreateKey(name))
        {
            try
            {
                CheckKey(key);
                Console.WriteLine("PASS: CurrentUser non-exportable RSA-3072 policy");
                bool denied=false;
                byte[] privateBytes=null;
                try { privateBytes=key.Export(CngKeyBlobFormat.Pkcs8PrivateBlob); }
                catch(CryptographicException) { denied=true; }
                finally
                {
                    if(privateBytes!=null) CryptographicOperations.ZeroMemory(privateBytes);
                }
                if(!denied) throw new InvalidOperationException("Private key export was not denied.");
                Console.WriteLine("PASS: private key export denied");
                using(var rsa=new RSACng(key))
                {
                    var catalog=NewCatalog();
                    RecipeFeedWire.ValidateCatalog(catalog);
                    byte[] payload=Json.Encode(catalog);
                    string publicKey=rsa.ExportSubjectPublicKeyInfoPem();
                    byte[] envelope=Envelope(payload,rsa);
                    RecipeFeedWire.Verify(envelope,publicKey);
                    Console.WriteLine("PASS: reviewed template signing and verification");
                    var altered=Json.Decode<SignedRecipeFeed>(envelope);
                    byte[] corrupt=Convert.FromBase64String(altered.payload);
                    corrupt[corrupt.Length/2]^=1;
                    altered.payload=Convert.ToBase64String(corrupt);
                    bool refused=false;
                    try { RecipeFeedWire.Verify(Json.Encode(altered),publicKey); }
                    catch(Unsupported) { refused=true; }
                    if(!refused) throw new InvalidOperationException("Tampered feed was accepted.");
                    Console.WriteLine("PASS: signed payload tampering rejected");
                    catalog.issued_utc=DateTime.UtcNow.AddDays(-1).ToString("O",CultureInfo.InvariantCulture);
                    catalog.expires_utc=DateTime.UtcNow.AddMinutes(-1).ToString("O",CultureInfo.InvariantCulture);
                    refused=false;
                    try { RecipeFeedWire.ValidateCatalog(catalog); }
                    catch(Unsupported) { refused=true; }
                    if(!refused) throw new InvalidOperationException("Expired catalog was accepted.");
                    Console.WriteLine("PASS: expired catalog rejected before signing");
                }
            }
            finally
            {
                // The randomly named key is created above by this invocation.
                // Never enumerate, delete or alter any other user's CNG key.
                key.Delete();
            }
        }
        if(CngKey.Exists(name,CngProvider.MicrosoftSoftwareKeyStorageProvider,CngKeyOpenOptions.None))
            throw new InvalidOperationException("Temporary signing key cleanup failed.");
        Console.WriteLine("PASS: only the temporary CNG key was removed");
        Console.WriteLine("Publisher self-test: 6 checks passed.");
    }
}
