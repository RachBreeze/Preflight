﻿(() => {

    const template = `
        <div class="card {{ cardClass }}">
            <span class="card-score {{ cardScoreClass }}" ng-bind="score"></span>
            <span class="card-title">
                {{ title }}<br />
                {{ subtitle }}
            </span>
        </div>`;

    function preflightCard() {
        const card = {
            restrict: 'E',
            scope: {
                title: '@?',
                subtitle: '@?',
                failed: '=',
                score: '='
            },
            template: template,
            link: scope => {
                if (scope.failed === true) {
                    scope.cardClass = 'fail';
                    scope.cardScoreClass = 'fail-color';
                } else if (scope.failed === false) {
                    scope.cardClass = 'pass';
                    scope.cardScoreClass = 'pass-color';
                }
            }
        };

        return card;
    }

    angular.module('umbraco').directive('preflightCard', preflightCard);

})();