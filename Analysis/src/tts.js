javascript: (function () {

function TextToSpeech(s) {
    import secrets from 'secrets.json' with { type: 'json' };
    var sModelId = "tts-1-hd";
    var sVoiceId = "echo";
    var API_KEY = secrets.API_KEY;

    ShowSpinner();

    var oHttp = new XMLHttpRequest();
    oHttp.open("POST", "https://api.openai.com/v1/audio/speech");
    oHttp.setRequestHeader("Accept", "audio/mpeg");
    oHttp.setRequestHeader("Content-Type", "application/json");
    oHttp.setRequestHeader("Authorization", "Bearer " + API_KEY);

    oHttp.onload = function () {
        if (oHttp.readyState === 4) {

            HideSpinner();

            var oBlob = new Blob([this.response], { "type": "audio/mpeg" });
            var audioURL = window.URL.createObjectURL(oBlob);
            var audio = new Audio();
            audio.src = audioURL;
            audio.play();
        }
    };

    var data = {
        model: sModelId,
        input: s,
        voice: sVoiceId
    };

    oHttp.responseType = "arraybuffer";
    oHttp.send(JSON.stringify(data));
}

function SpeakText(s) {
    var msg = new SpeechSynthesisUtterance(s);
    window.speechSynthesis.speak(msg);
}

function ShowSpinner() {
    var o = document.getElementById('spinnerContainer');
    if (o) return;

    var style = document.getElementById('SpinnerStyle');
    if (style == null) {
        var style = document.createElement("style");
        style.id = "SpinnerStyle";
        style.textContent = ".spinner-container { position: fixed; top: 50%;
        left: 50%; transform: translate(-50%, -50%); z-index: 9999;}"
                + ".spinner {border: 4px solid rgba(0, 0, 0, 0.1);
                border-left: 4px solid #3498db; border-radius: 50%; width: 40px;
                height: 40px; animation: spin 1s linear infinite;}"
                          + "@keyframes spin {0% {transform: rotate(0deg);}
                             100% {transform: rotate(360deg);}}";
        document.head.appendChild(style);
    }

    var c = document.createElement('div');
    c.id = 'spinnerContainer';
    c.classList.add('spinner-container');
    var s = document.createElement('div');
    s.classList.add('spinner');
    c.appendChild(s);
    document.body.appendChild(c);
}

function HideSpinner() {
    var o = document.getElementById('spinnerContainer');
    if (o) {
        document.body.removeChild(o);
    }
}

var s = window.getSelection().toString();
if (s) {
    TextToSpeech(s);
} else {
    alert('Please select some text to speak.');
}

}) ();