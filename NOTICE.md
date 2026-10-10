# Third-party notices

## AudioPlaybackConnector

SoundBridge's Bluetooth connection flow is adapted from
[AudioPlaybackConnector](https://github.com/ysc3839/AudioPlaybackConnector) by Richard Yu.

What was adapted: the sequence for receiving audio through Windows' A2DP sink — enumerate devices with
`AudioPlaybackConnection.GetDeviceSelector()`, create a connection with `TryCreateFromId`, watch `StateChanged`,
call `StartAsync` then `OpenAsync`, map the open-result statuses, and close the connection to disconnect. In
SoundBridge this lives in `src/SoundBridge/Bluetooth/BluetoothAudioLink.cs` and
`src/SoundBridge/Bluetooth/AudioSourceWatcher.cs`, rewritten in C#. The tray-icon handling (re-adding the icon
when Explorer restarts, following the taskbar's light/dark mode) follows the same project's approach.

No upstream source files are included. AudioPlaybackConnector is distributed under the MIT License:

```
MIT License

Copyright (c) 2020 Richard Yu

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Microsoft .NET and the Windows SDK projections

SoundBridge runs on .NET and calls Windows Runtime APIs through Microsoft's Windows SDK projection
(`Microsoft.Windows.SDK.NET`, C#/WinRT). Both are distributed by Microsoft under the MIT License.
