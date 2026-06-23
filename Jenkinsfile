
library 'ts-jenkins-shared-library@main'

pipeline {
    agent none
    options {
        copyArtifactPermission('*/TownSuite-Artifact-Publish')
        buildDiscarder(logRotator(numToKeepStr: '10'))
        timestamps()
        timeout(time: 2, unit: 'HOURS')
    }
    stages {
        stage('Start Automation Script') {
            agent { label 'starting-agent' }
            steps {
                script {
                    townsuite_automation2.start_linux()
                }
            }
        }
        stage('Pipeline') {
            agent { label townsuite_automation2.get_linux_label() }
            stages {
                stage('Environment Setup') {
                    steps {
                        script {
                            townsuite.common_environment_configuration()
                            townsuite.checkout_scm()
                        }
                    }
                }
                stage('Build') {
                    steps {
                        sh 'dotnet build TownSuite.WorkQueues.sln --configuration Release'
                    }
                }
                stage('Test') {
                    steps {
                        sh '''
                        dotnet test TownSuite.WorkQueues.Testing/TownSuite.WorkQueues.Testing.csproj \
                            --configuration Release \
                            --no-build \
                            --logger "nunit"
                        '''
                    }
                    post {
                        always {
                            nunit testResultsPattern: '**/TestResults/*.xml'
                        }
                    }
                }
                stage('Code Sign') {
                    when {
                        expression { return env.BRANCH_NAME.startsWith('PR-') == false }
                    }
                    steps {
                        echo 'Code Signing happening here....'
                        script {
                            townsuite.codesign "${env.WORKSPACE}", "*TownSuite*.dll;*TownSuite*.exe", false
                        }
                    }
                }
                stage('NuGet Pack') {
                    steps {
                        sh '''
                        mkdir -p build
                        dotnet pack TownSuite.WorkQueues/TownSuite.WorkQueues.csproj \
                            --configuration Release --no-build --output build
                        dotnet pack TownSuite.WorkQueues.Postgres/TownSuite.WorkQueues.Postgres.csproj \
                            --configuration Release --no-build --output build
                        dotnet pack TownSuite.WorkQueues.SqlServer/TownSuite.WorkQueues.SqlServer.csproj \
                            --configuration Release --no-build --output build
                        dotnet pack TownSuite.WorkQueues.Redis/TownSuite.WorkQueues.Redis.csproj \
                            --configuration Release --no-build --output build
                        dotnet pack TownSuite.WorkQueues.Sqlite/TownSuite.WorkQueues.Sqlite.csproj \
                            --configuration Release --no-build --output build
                        '''
                    }
                }
                stage('Code Sign Detached') {
                    when {
                        expression { return env.BRANCH_NAME.startsWith('PR-') == false }
                    }
                    steps {
                        echo 'Code Signing happening here....'
                        script {
                            townsuite.codesign "${env.WORKSPACE}/build", "*.nupkg", true
                        }
                    }
                }
                stage('Archive') {
                    steps {
                        echo 'archiving artifacts'
                        script {
                            townsuite.archiveWithRetryAndLock('build/*.SHA256SUMS,build/*.sig,build/*.nupkg,build/parameterproperties.txt', 3)
                        }
                    }
                }
            }
        }
    }
    post {
        always {
            CleanupVirtualMachines()
        }
        success {
            echo 'Pipeline executed successfully.'
        }
        failure {
            echo 'Pipeline failed.'
        }
        aborted {
            echo 'Pipeline was aborted.'
        }
    }
}

def CleanupVirtualMachines() {
    node('stopping-agent') {
        cleanWs()
        script {
            townsuite_automation2.stop_automation()
        }
    }
}
