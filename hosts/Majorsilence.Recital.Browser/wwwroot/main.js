import { dotnet } from './_framework/dotnet.js'

const dotnetRuntime = await dotnet
    .withDiagnosticTracing(false)
    .create();

const config = dotnetRuntime.getConfig();

// Reading aloud with the browser's own voices (WebSpeechEngine.cs). Utterances are kept referenced until
// they end: some browsers otherwise collect them and never fire their events.
const synth = globalThis.speechSynthesis;
const speaking = new Set();
let speechExports = null;
const report = (channel, chunk, kind, charIndex) =>
    speechExports?.Majorsilence.Recital.Browser.WebSpeechEngine.OnEvent(channel, chunk, kind, charIndex);

dotnetRuntime.setModuleImports('recital-speech', {
    available: () => !!synth && typeof SpeechSynthesisUtterance !== 'undefined',
    // Voices load asynchronously in Chrome: wait for voiceschanged, but never more than a second.
    voices: () => new Promise(resolve => {
        const list = () => JSON.stringify(synth.getVoices().map(v => ({ id: v.voiceURI, name: v.name, lang: v.lang })));
        if (synth.getVoices().length > 0) { resolve(list()); return; }
        const done = () => { synth.removeEventListener('voiceschanged', done); resolve(list()); };
        synth.addEventListener('voiceschanged', done);
        setTimeout(done, 1000);
    }),
    speak: (channel, chunk, text, lang, voice, rate) => {
        const u = new SpeechSynthesisUtterance(text);
        if (lang) u.lang = lang;
        const chosen = voice ? synth.getVoices().find(v => v.voiceURI === voice) : null;
        if (chosen) u.voice = chosen;
        u.rate = rate;
        u.onstart = () => report(channel, chunk, 'start', 0);
        u.onboundary = e => { if (e.name === 'word') report(channel, chunk, 'word', e.charIndex); };
        u.onend = () => { speaking.delete(u); report(channel, chunk, 'end', 0); };
        u.onerror = e => {
            speaking.delete(u);
            // Cancelling (stop, or a new channel) interrupts utterances; that is not a failure.
            report(channel, chunk, e.error === 'canceled' || e.error === 'interrupted' ? 'end' : e.error, 0);
        };
        speaking.add(u);
        synth.speak(u);
    },
    pause: () => synth.pause(),
    resume: () => synth.resume(),
    cancel: () => synth.cancel(),
});

speechExports = await dotnetRuntime.getAssemblyExports(config.mainAssemblyName);

await dotnetRuntime.runMain(config.mainAssemblyName, []);
