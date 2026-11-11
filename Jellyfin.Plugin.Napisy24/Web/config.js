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
            Dashboard.hideLoadingMsg();
        });
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
