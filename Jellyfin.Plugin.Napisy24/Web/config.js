const pluginId = '1e3057d0-c2e0-4bbc-a67b-5fa31cdbebd9';

function setValue(view, id, value) {
    const element = view.querySelector('#' + id);
    if (element) {
        element.value = value;
    }
}

function setChecked(view, id, value) {
    const element = view.querySelector('#' + id);
    if (element) {
        element.checked = !!value;
    }
}

function renderStatus(view, status) {
    const statusElement = view.querySelector('#activationStatus');
    const serverElement = view.querySelector('#serverId');
    const messageElement = view.querySelector('#activationMessage');

    statusElement.innerText = status.IsActivated ? 'ACTIVE' : 'INACTIVE';
    statusElement.style.color = status.IsActivated ? '#4caf50' : '#ff5252';
    serverElement.innerText = status.ServerId || '';

    if (status.IsActivated) {
        let message = 'Activated';
        if (status.Name) {
            message += ' for ' + status.Name;
        }
        if (status.Expires) {
            message += ', expires ' + status.Expires;
        }
        messageElement.innerText = message;
        messageElement.style.color = '#4caf50';
    } else {
        messageElement.innerText = status.Error || '';
        messageElement.style.color = '#ff5252';
    }
}

function refreshStatus(view) {
    return ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('Napisy24/Status') })
        .then(function (response) {
            return response.json();
        })
        .then(function (status) {
            renderStatus(view, status);
            return status;
        })
        .catch(function () {
            view.querySelector('#activationStatus').innerText = 'UNKNOWN';
            view.querySelector('#activationMessage').innerText = 'Could not read the activation status.';
        });
}

function activate(view) {
    const code = view.querySelector('#activationCode').value.trim();
    if (!code) {
        view.querySelector('#activationMessage').innerText = 'Enter an activation code first.';
        return;
    }

    ApiClient.ajax({
        type: 'POST',
        url: ApiClient.getUrl('Napisy24/Activate'),
        data: JSON.stringify({ Code: code }),
        contentType: 'application/json'
    }).then(function (response) {
        return response.json().then(function (status) {
            renderStatus(view, status);
            if (response.ok && status.IsActivated) {
                view.querySelector('#activationMessage').innerText = 'Activated.';
            }
        });
    }).catch(function () {
        view.querySelector('#activationMessage').innerText = 'Activation failed. Check the code and try again.';
    });
}

function deactivate(view) {
    ApiClient.ajax({ type: 'POST', url: ApiClient.getUrl('Napisy24/Deactivate') })
        .then(function (response) {
            return response.json();
        })
        .then(function (status) {
            renderStatus(view, status);
        })
        .catch(function () {
            view.querySelector('#activationMessage').innerText = 'Could not deactivate the plugin.';
        });
}

export default function (view) {
    view.addEventListener('viewshow', function () {
        Dashboard.showLoadingMsg();

        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            setValue(view, 'baseUrl', config.BaseUrl || 'https://napisy24.pl');
            setChecked(view, 'enableHashLookup', config.EnableHashLookup);
            setValue(view, 'agentUsername', config.AgentUsername || '');
            setValue(view, 'agentPassword', config.AgentPassword || '');
            setChecked(view, 'useMd5Fallback', config.UseMd5Fallback);
            setChecked(view, 'enableMetadataSearch', config.EnableMetadataSearch);
            setChecked(view, 'enableAutomaticMetadataSearch', config.EnableAutomaticMetadataSearch);
            setValue(view, 'maxResults', config.MaxResults || 10);
            setValue(view, 'requestDelayMs', config.RequestDelayMs);
            setValue(view, 'autoScoreThreshold', config.AutoScoreThreshold);
            setChecked(view, 'mergeMultiCd', config.MergeMultiCd);
            setValue(view, 'activationCode', config.ActivationCode || '');
            Dashboard.hideLoadingMsg();
        });

        refreshStatus(view);
    });

    view.querySelector('#activateButton').addEventListener('click', function () {
        activate(view);
    });

    view.querySelector('#deactivateButton').addEventListener('click', function () {
        deactivate(view);
    });

    view.querySelector('#Napisy24ConfigForm').addEventListener('submit', function (event) {
        event.preventDefault();
        Dashboard.showLoadingMsg();

        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            config.BaseUrl = view.querySelector('#baseUrl').value.trim() || 'https://napisy24.pl';
            config.EnableHashLookup = view.querySelector('#enableHashLookup').checked;
            config.AgentUsername = view.querySelector('#agentUsername').value.trim();
            config.AgentPassword = view.querySelector('#agentPassword').value;
            config.UseMd5Fallback = view.querySelector('#useMd5Fallback').checked;
            config.EnableMetadataSearch = view.querySelector('#enableMetadataSearch').checked;
            config.EnableAutomaticMetadataSearch = view.querySelector('#enableAutomaticMetadataSearch').checked;
            config.MaxResults = parseInt(view.querySelector('#maxResults').value, 10) || 10;
            config.RequestDelayMs = parseInt(view.querySelector('#requestDelayMs').value, 10) || 0;
            config.AutoScoreThreshold = parseInt(view.querySelector('#autoScoreThreshold').value, 10) || 0;
            config.MergeMultiCd = view.querySelector('#mergeMultiCd').checked;

            ApiClient.updatePluginConfiguration(pluginId, config).then(function (result) {
                Dashboard.processPluginConfigurationUpdateResult(result);
            });
        });

        return false;
    });
}
