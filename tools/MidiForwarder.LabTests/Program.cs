#if TEST_LAB_OFFLINE_WINDOWS
if (args.Length != 2) throw new ArgumentException("The offline profile accepts only Functional test execution.");
return PortableXunit.Run(args, ["MidiForwarder.Core.Tests", "MidiForwarder.CoreMidi.Tests"]);
#else
return PortableXunit.Run(args, ["MidiForwarder.Core.Tests", "MidiForwarder.Client.Tests", "MidiForwarder.Server.Tests", "MidiForwarder.CoreMidi.Tests"]);

#endif
