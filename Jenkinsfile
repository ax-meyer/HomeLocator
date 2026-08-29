pipeline {
    agent any
    environment {
        IMAGE_NAME = "homelocator"
        IMAGE_TAG = "latest"
        REMOTE_HOST = "192.168.0.156"
        REMOTE_PATH = "/container/homelocator"
        REMOTE_CONTAINER_NAME = "homelocator"
        EXTERNAL_PORT = "8085"
        INTERNAL_PORT = "8080"
        DOCKER_NETWORK = "haproxy_network"
        SERVICE_NAME = "app"
        PUBLIC_BASE_PATH = "/homelocator"
        LOCAL_IMAGE_TAR = "homelocator.tar"
    }
    options {
        buildDiscarder(logRotator(numToKeepStr: '5', artifactDaysToKeepStr: '15'))
    }
    triggers {
        pollSCM('H/5 * * * *')
    }
    stages {
        stage('Checkout Code') {
            steps {
                checkout scm
            }
        }
        stage('Build Image') {
            steps {
                sh 'docker build --platform linux/amd64 -f Grundstuecksfinder/Dockerfile -t ${IMAGE_NAME}:${IMAGE_TAG} .'
            }
        }
        stage('Prepare Remote Directories') {
            steps {
                withCredentials([usernamePassword(credentialsId: '5b17f8ac-9503-436b-ae6d-387119dc9fe3', usernameVariable: 'USERNAME', passwordVariable: 'PASSWORD')]) {
                    sh '''
                    sshpass -p "$PASSWORD" ssh -o StrictHostKeyChecking=no "$USERNAME@$REMOTE_HOST" \
                        "mkdir -p $REMOTE_PATH && chmod 755 $REMOTE_PATH"
                    '''
                }
            }
        }
        stage('Upload Application') {
            steps {
                withCredentials([usernamePassword(credentialsId: '5b17f8ac-9503-436b-ae6d-387119dc9fe3', usernameVariable: 'USERNAME', passwordVariable: 'PASSWORD')]) {
                    sh '''
                    sshpass -p "$PASSWORD" scp -o StrictHostKeyChecking=no \
                        docker-compose.yml .env.example \
                        "$USERNAME@$REMOTE_HOST:$REMOTE_PATH/"
                    echo "cd $REMOTE_PATH && test -f .env || cp .env.example .env && sed -i \"s#^EXTERNAL_PORT=.*#EXTERNAL_PORT=$EXTERNAL_PORT#\" .env && sed -i \"s#^DOCKER_NETWORK=.*#DOCKER_NETWORK=$DOCKER_NETWORK#\" .env && if grep -q '^DB_PASSWORD=changeme' .env; then pass=\\$(head /dev/urandom | tr -dc A-Za-z0-9 | head -c 24); sed -i \"s#^DB_PASSWORD=.*#DB_PASSWORD=\\$pass#\" .env; fi" \
                        | sshpass -p "$PASSWORD" ssh -o StrictHostKeyChecking=no "$USERNAME@$REMOTE_HOST" bash -s
                    '''
                }
            }
        }
        stage('Transfer Runtime Image') {
            steps {
                withCredentials([usernamePassword(credentialsId: '5b17f8ac-9503-436b-ae6d-387119dc9fe3', usernameVariable: 'USERNAME', passwordVariable: 'PASSWORD')]) {
                    sh '''
                    docker save $IMAGE_NAME:$IMAGE_TAG -o $LOCAL_IMAGE_TAR
                    sshpass -p "$PASSWORD" scp -o StrictHostKeyChecking=no $LOCAL_IMAGE_TAR \
                        "$USERNAME@$REMOTE_HOST:$REMOTE_PATH/"
                    sshpass -p "$PASSWORD" ssh -o StrictHostKeyChecking=no "$USERNAME@$REMOTE_HOST" \
                        "docker load -i $REMOTE_PATH/$LOCAL_IMAGE_TAR && rm -f $REMOTE_PATH/$LOCAL_IMAGE_TAR"
                    '''
                }
            }
        }
        stage('Deploy') {
            steps {
                withCredentials([usernamePassword(credentialsId: '5b17f8ac-9503-436b-ae6d-387119dc9fe3', usernameVariable: 'USERNAME', passwordVariable: 'PASSWORD')]) {
                    sh '''
                    echo "cd $REMOTE_PATH && docker network inspect $DOCKER_NETWORK >/dev/null 2>&1 || docker network create $DOCKER_NETWORK && docker compose up -d --no-build --force-recreate $SERVICE_NAME db && docker compose ps" \
                        | sshpass -p "$PASSWORD" ssh -o StrictHostKeyChecking=no "$USERNAME@$REMOTE_HOST" bash -s
                    '''
                }
            }
        }
        stage('Verify Deployment') {
            steps {
                withCredentials([usernamePassword(credentialsId: '5b17f8ac-9503-436b-ae6d-387119dc9fe3', usernameVariable: 'USERNAME', passwordVariable: 'PASSWORD')]) {
                    sh '''
                    echo "for attempt in \\$(seq 1 20); do curl --fail --silent http://127.0.0.1:$EXTERNAL_PORT/health && exit 0; sleep 5; done; docker logs --tail 100 homelocator; exit 1" \
                        | sshpass -p "$PASSWORD" ssh -o StrictHostKeyChecking=no "$USERNAME@$REMOTE_HOST" bash -s
                    '''
                }
            }
        }
    }
    post {
        always { sh 'rm -f ${LOCAL_IMAGE_TAR}' }
        success { echo 'HomeLocator deployment succeeded.' }
        failure { echo 'HomeLocator deployment failed. Check the build log.' }
    }
}
