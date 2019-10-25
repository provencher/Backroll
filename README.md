# Backroll

Backroll is a reimplementation of [GGPO](http://ggpo.net) in C#, made explicitly
for Unity, and retooled to use
[Hourai Networking](https://github.com/HouraiTeahouse/HouraiNetworking) as a
transport level abstraction layer.

Backroll is currently alpha-quality software. Expect bugs and use at your own
risk.

## Installation
Backroll is most easily installable via Unity Package Manager. In Unity 2018.3+,
add the following to your `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.houraiteahouse.backroll": "0.1.0"
  },
  "scopedRegistries": [
    {
      "name": "Hourai Teahouse",
      "url": "https://upm.houraiteahouse.net",
      "scopes": ["com.houraiteahouse"]
    }
  ]
}
```

NOTE: Backroll is compiled with specific constants which apply hard limitations
on the kind of data it can handle, like the maximum number of players in a given
game and in-memory size of a input.Generally speaking, these are common sense
limitations, growing outside these limitations may suggest that Backroll is not
the correct solution you're looking for. If that does not deter you, it is then
suggested to directly embed the package under your project's `Packages/`
directory by git cloning it there and directly editing the source code.

## Usage
TODO(james7132): Document this

## FAQ

### What's GGPO?
Networking real-time games is hard. Latency in fast paced games can be a serious
issue for players. For example, fast paced fighting games often require reaction
windows of less than 100 ms. With the round trip time of many network
connections stretching into 200-500 ms of latency, traditional methods of
networking (i.e. lockstep simulation, frame delay, etc.) may introduce
alterations to gameplay that negatively impact player experience.

Rollback netcode attempts to remedy this with speculative execution: it attempts
to predict remote inputs based on prior inputs. This allows games to continue
simulation as if the game were entirely local, rolling back and replaying the
simulation only if recieved inputs differ from the prediction. Players only
notice the network latency if the predictions are wrong, making for a much
smoother online experience.

GGPO (Good Game Peace Out) was one of the original implemenations of rollback
netcode originally intended for use in fighting games, targetting only

### Why port it to C#/Unity instead of just creating bindings?
GGPO currently only builds on Windows, being directly dependent on Win32 APIs.
Unity offers one of the best options when building cross platform games.
Reimplementing GGPO as Backroll in Unity enables much easier cross platform
networking solutions.

Unity also added unsafe raw pointer manipulation utilities commonly used in
C/C++ (memcpy, mpmset, etc.) commonly used across GGPO's source code. This
allows Backroll to maintain the largely the same performance profile as the
original GGPO.

Tangentially related: porting to C# allows use of Hourai Networking whch enables
the transport layer to be abstracted. The original GGPO used a raw UDP
communication layer, that developers could replace with their own by rewriting
the UdpProtocol class as needed. This however, requires a complete understanding
of both GGPO's source code. Hourai Networking adds one level of indirection and
makes Backroll agnostic to the underlying network implemenation. This makes
integrations with platform specific networks like Steam or Discord trivial.

## Licensing
Backroll is available under The MIT License. This means Backroll is free for
commercial and non-commercial use. Attribution is not required, but appreciated.
