$(function () {
    var hub = $.connection.gameHub;
    hub.client.reply = function (score) {
        $('#reply').text(score);
    };
    $.connection.hub.start().done(function () {
        hub.server.play('red', 'green');
    });
});