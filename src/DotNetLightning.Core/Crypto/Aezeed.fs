namespace DotNetLightning.Crypto

open System
open System.Collections
open System.Diagnostics
open System.IO
open System.Runtime.CompilerServices

open NBitcoin

open DotNetLightning.Core.Utils.Extensions
open DotNetLightning.Serialize
open DotNetLightning.Utils

type O = OptionalArgumentAttribute
type D = System.Runtime.InteropServices.DefaultParameterValueAttribute

[<AutoOpen>]
module private Constants =
    /// CipherSeedVersion is the current version of the aezeed scheme as defined in this package.
    /// This version indicates the following parameters for the deciphered cipher seed: a 1 byte version, 2 bytes
    /// for the Bitcoin Days Genesis timestamp, and 16 bytes for entropy. It also governs how the cipher seed
    [<Literal>]
    let CIPHER_SEED_VERSION = 0uy
    
    [<Literal>]
    let DECIPHERED_CIPHER_SEED_SIZE = 19
    
    [<Literal>]
    let ENCIPHERED_CIPHER_SEED_SIZE = 33
    
    [<Literal>]
    let CIPHER_TEXT_EXPANSION = 4
    
    [<Literal>]
    let ENTROPY_SIZE = 16
    
    [<Literal>]
    let NUM_MNEMONIC_WORDS = 24

    [<Literal>]
    let SALT_SIZE = 5
    
    [<Literal>]
    let AD_SIZE = 6
    
    [<Literal>]
    let CHECKSUM_SIZE = 4
    
    [<Literal>]
    let KEY_LEN = 32
    
    [<Literal>]
    let BITS_PER_WORD = 11
    
    let SALT_OFFSET = ENCIPHERED_CIPHER_SEED_SIZE - CHECKSUM_SIZE - SALT_SIZE
    
    let CHECKSUM_OFFSET = ENCIPHERED_CIPHER_SEED_SIZE - CHECKSUM_SIZE
    
    let ENCIPHERED_SEED_SIZE = DECIPHERED_CIPHER_SEED_SIZE + CIPHER_TEXT_EXPANSION
    
    [<Literal>]
    let V0_SCRYPT_N = 32768
    
    [<Literal>]
    let V0_SCRYPT_R = 8
    
    [<Literal>]
    let V0_SCRYPT_P = 1
    
    let DEFAULT_PASPHRASE =
        let ascii = NBitcoin.DataEncoders.ASCIIEncoder()
        "aezeed" |> ascii.DecodeData
    
    let BITCOIN_GENESIS_DATE = NBitcoin.Network.Main.GetGenesis().Header.BlockTime
    
    let crc = AEZ.Crc32()
    
    let convertBits(data, fromBits, toBits, pad: bool) =
        InternalBech32Encoder.Instance.ConvertBits(data, fromBits, toBits, pad)
        
#if BouncyCastle
    let getScryptKey(passphrase, salt) =
        Org.BouncyCastle.Crypto.Generators.SCrypt.Generate(passphrase, salt, V0_SCRYPT_N, V0_SCRYPT_R, V0_SCRYPT_P, KEY_LEN)
#else
    let scrypt = NSec.Experimental.PasswordBased.Scrypt(int64 V0_SCRYPT_N, V0_SCRYPT_R, V0_SCRYPT_P)
    let getScryptKey(passphrase, salt) =
        scrypt.DeriveBytes(passphrase.AsSpan().ToString(), ReadOnlySpan(salt), KEY_LEN)
#endif
type AezeedError =
    | UnsupportedVersion of uint8
    | InvalidPass of byte[]
    | IncorrectMnemonic of expectedCheckSum: uint32 * actualChecksum: uint32
    
[<AutoOpen>]
module private Helpers = 
    let private extractAD(encipheredSeed: byte[]) =
        let ad = Array.zeroCreate AD_SIZE
        ad.[0] <- encipheredSeed.[0]
        let a = encipheredSeed.[SALT_OFFSET..CHECKSUM_OFFSET - 1]
        a.AsSpan().CopyTo(ad.[1..].AsSpan())
        ad
        
    let decipherCipherSeed(cipherSeedBytes: byte[], password: byte[]) =
        Debug.Assert(cipherSeedBytes.Length = DECIPHERED_CIPHER_SEED_SIZE)
        if cipherSeedBytes.[0] <> CIPHER_SEED_VERSION then Error(AezeedError.UnsupportedVersion cipherSeedBytes.[0]) else
        let salt = cipherSeedBytes.[SALT_OFFSET..SALT_OFFSET + SALT_SIZE - 1]
        let cipherSeed = cipherSeedBytes.[1..SALT_OFFSET - 1]
        let checkSum = cipherSeedBytes.[CHECKSUM_OFFSET..] |> UInt32.FromBytesBigEndian
        let freshChecksum = crc.Get(cipherSeedBytes.[..CHECKSUM_OFFSET - 1])
        if (freshChecksum <> checkSum) then Error(AezeedError.IncorrectMnemonic(freshChecksum, checkSum)) else
        let key = getScryptKey(password, salt)
        let ad = extractAD(cipherSeedBytes)
        let r = (AEZ.AEZ.Decrypt(ReadOnlySpan(key), ReadOnlySpan.Empty ,[|ad|], CIPHER_TEXT_EXPANSION, ReadOnlySpan(cipherSeed), Span.Empty))
        Ok(r.ToArray())
        
    let mnemonicToCipherText(mnemonic: string[], lang: Wordlist option) =
        let lang = Option.defaultValue NBitcoin.Wordlist.English lang
        let indices = lang.ToIndices mnemonic
        let cipherBits = BitWriter()
        for i in indices do
            let b = (uint32 i).GetBytesBigEndian()
            cipherBits.Write(b, BITS_PER_WORD)
        cipherBits.ToBytes()
        
    let cipherTextToMnemonic(cipherText: byte[], lang: Wordlist option) =
        Debug.Assert(cipherText.Length = ENCIPHERED_CIPHER_SEED_SIZE)
        let lang = Option.defaultValue NBitcoin.Wordlist.English lang
        let words = Array.zeroCreate NUM_MNEMONIC_WORDS
        let reader = BitReader(BitArray(cipherText), ENCIPHERED_CIPHER_SEED_SIZE * 8)
        for i in 0..(NUM_MNEMONIC_WORDS) do
            let index = (reader.ReadBits(BITS_PER_WORD)).ToByteArray() |> UInt32.FromBytesBigEndian
            words.[i] <- lang.GetWordAtIndex(int index)
        words


        
[<Struct>]
type CipherSeed = internal {
    InternalVersion: uint8
    _Birthday: uint16
    Entropy: byte[]
    Salt: byte[]
}
    with
    static member Create(internalVersion: uint8, entropy: byte[] option, now: DateTimeOffset) =
        let entropy = Option.defaultWith (fun _ -> RandomUtils.GetBytes(ENTROPY_SIZE)) entropy
        if entropy.Length < ENTROPY_SIZE then raise <| ArgumentException(sprintf "entropy size must be at least %d! it was %d" ENTROPY_SIZE entropy.Length)
        let seed = Array.zeroCreate ENTROPY_SIZE
        Array.blit entropy 0 seed 0 ENTROPY_SIZE
        
        let birthDate = uint16((now - BITCOIN_GENESIS_DATE).Days)
        {
            CipherSeed.InternalVersion = internalVersion
            _Birthday = birthDate
            Entropy = seed
            Salt = RandomUtils.GetBytes SALT_SIZE
        }
        
    member this.Serialize(ls: LightningWriterStream) =
        ls.Write(this.InternalVersion)
        ls.Write(this._Birthday, false)
        ls.Write(this.Entropy)
    member this.ToBytes() =
        use ms = new MemoryStream()
        use ls = new LightningWriterStream(ms)
        this.Serialize ls
        ms.ToArray()
        
    static member Deserialize(ls: LightningReaderStream) =
        {
            InternalVersion = ls.ReadByte()
            _Birthday = ls.ReadUInt16(false)
            Entropy = ls.ReadBytes(ENTROPY_SIZE)
            Salt = Array.zeroCreate SALT_SIZE
        }
        
    member private this.GetADBytes() =
        let res = Array.zeroCreate (SALT_SIZE + 1)
        res.[0] <- byte CIPHER_SEED_VERSION
        Array.blit res 1 this.Salt 0 this.Salt.Length
        res
        
    static member FromBytes(b: byte[]) =
        use ms = new MemoryStream(b)
        use ls = new LightningReaderStream(ms)
        CipherSeed.Deserialize ls

    member this.Encipher() = this.Encipher(None)
        
    /// Takes a fully populated cipherseed instance, and enciphers the
    /// encoded seed, then appends a randomly generated seed used to stretch th
    /// passphrase out into an appropriate key, then computes a checksum over the
    /// preceding. Returns 33 bytes enciphered cipherseed
    member this.Encipher(password: byte[] option): byte[] =
        let result = Array.zeroCreate ENCIPHERED_CIPHER_SEED_SIZE
        let passphrase = Option.defaultValue DEFAULT_PASPHRASE password
        
        let key = getScryptKey(passphrase, this.Salt)
        
        let seedBytes = this.ToBytes()
        let ad = this.GetADBytes()
        let cipherText = AEZ.AEZ.Encrypt(ReadOnlySpan(key), ReadOnlySpan.Empty, [| ad |], CIPHER_TEXT_EXPANSION, ReadOnlySpan(seedBytes), Span.Empty)
        result.[0] <- byte CIPHER_SEED_VERSION
        cipherText.CopyTo(result.AsSpan().Slice(1, SALT_OFFSET))
        this.Salt.CopyTo(result.AsSpan().Slice(SALT_OFFSET))
        
        let checkSum = crc.Get(result)
        checkSum.GetBytesBigEndian().CopyTo(result.AsSpan().Slice(CHECKSUM_OFFSET))
        result
        
    member this.ToMnemonicWords(password: byte[] option, lang: Wordlist option) =
        let cipherText = this.Encipher(password)
        cipherTextToMnemonic(cipherText, lang)
        
    member this.ToMnemonicWords([<O; D(null)>] password, [<O; D(null)>] lang) =
        let pass = if isNull password then None else Some(password)
        let lang = if isNull lang then None else Some(lang)
        this.ToMnemonicWords(pass, lang)
        
    member this.ToMnemonic(password: byte[] option, lang: Wordlist option) =
        this.ToMnemonicWords(password, lang) |> Seq.fold(fun x acc -> x + " " + acc) "" |> Mnemonic
        
    member this.ToMnemonic([<O;D(null)>]password, [<O;D(null)>]lang) =
        let pass = if isNull password then None else Some(password)
        let lang = if isNull lang then None else Some(lang)
        this.ToMnemonic(pass, lang)
        
    member this.BirthDay =
        let offset = TimeSpan.FromDays(float this._Birthday)
        BITCOIN_GENESIS_DATE + offset
        
[<Extension;Sealed;AbstractClass>]
type MnemonicExtensions =

    [<Extension>]
    /// Attempts to map the mnemonic to the original cipher text byte slice.
    /// Then It will attempt to decrypt the ciphertext using aez with the passed passphrase,
    /// using the last 5 bytes of the ciphertext as a salt for the KDF.
    static member ToCipherSeed(this: Mnemonic, password: byte[] option, lang: Wordlist option) =
        this.Decipher(password, lang)
        |> Result.map CipherSeed.FromBytes
        
    [<Extension>]
        
    static member private Decipher(this: Mnemonic, password: byte[] option, lang: Wordlist option) =
        let pass = Option.defaultValue DEFAULT_PASPHRASE password
        let  cipherText = mnemonicToCipherText (this.Words, lang)
        decipherCipherSeed(cipherText, pass)
        
    [<Extension>]
    static member ChangePass(this: Mnemonic, oldPass: byte[], newPass: byte[], lang: Wordlist option) =
        this.ToCipherSeed(Some oldPass, lang)
        |> Result.map(fun x -> x.ToMnemonic newPass)
